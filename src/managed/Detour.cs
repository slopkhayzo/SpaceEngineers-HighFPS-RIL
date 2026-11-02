using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SpaceEngineers.HighFPSFix
{
    internal sealed class DetourException : Exception
    {
        public DetourException(string message) : base(message) { }
    }

    // All hooks of one feature, or none. Hooks are created and queued, then
    // enabled together by Commit (one freeze of the other threads); disposing
    // without a successful Commit removes them again.
    internal sealed class HookTransaction : IDisposable
    {
        private readonly System.Collections.Generic.List<IntPtr> m_targets = new System.Collections.Generic.List<IntPtr>();
        private bool m_committed;

        public IntPtr Install(MethodBase target, MethodBase detour)
        {
            IntPtr original = Detour.Create(target, detour, out IntPtr targetCode);
            m_targets.Add(targetCode);
            return original;
        }

        public void Commit()
        {
            int status = NativeApi.ApplyQueued();
            if (status != 0)
                throw new DetourException($"enabling the hooks failed (MinHook status {status})");
            m_committed = true;
        }

        public void Dispose()
        {
            if (m_committed)
                return;
            foreach (IntPtr target in m_targets)
                NativeApi.RemoveHook(target);
            m_targets.Clear();
        }
    }

    // Hooks the native code the JIT produced for a game method. The detour is
    // the JIT code of one of our static methods with the same managed
    // signature (an instance method's 'this' becomes the first parameter); the
    // returned trampoline calls the original and is invoked through a managed
    // function pointer (delegate*<...>), so no transition or marshalling is
    // involved on either path.
    //
    // .NET Framework 4.8 has no tiered compilation or re-JIT, so the code a
    // method gets on its first JIT is the code it keeps.
    internal static unsafe class Detour
    {
        public static IntPtr NativeCode(MethodBase method)
        {
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            byte* code = (byte*)method.MethodHandle.GetFunctionPointer();
            // GetFunctionPointer may return a precode stub that jumps to the
            // JIT code; follow those jumps to the method body itself.
            for (int hop = 0; hop < 8 && code != null; hop++)
            {
                if (code[0] == 0xE9)
                    code = code + 5 + *(int*)(code + 1);                      // jmp rel32
                else if (code[0] == 0xFF && code[1] == 0x25)
                    code = *(byte**)(code + 6 + *(int*)(code + 2));            // jmp [rip+rel32]
                else if (code[0] == 0x49 && code[1] == 0xBA && code[10] == 0xE9)
                    code = code + 15 + *(int*)(code + 11);                     // mov r10, md; jmp rel32
                else if (code[0] == 0x48 && code[1] == 0xB8 && code[10] == 0xFF && code[11] == 0xE0)
                    code = *(byte**)(code + 2);                                // mov rax, imm64; jmp rax
                else
                    break;
            }
            return (IntPtr)code;
        }

        // Creates and enables one hook (the self-test); features use a
        // HookTransaction.
        public static IntPtr Install(MethodBase target, MethodBase detour)
        {
            IntPtr original = Create(target, detour, out IntPtr targetCode);
            int status = NativeApi.ApplyQueued();
            if (status != 0)
            {
                NativeApi.RemoveHook(targetCode);
                throw new DetourException($"enabling the hook failed (MinHook status {status})");
            }
            return original;
        }

        // Creates a hook and queues it for enabling.
        public static IntPtr Create(MethodBase target, MethodBase detour, out IntPtr targetCode)
        {
            if (target == null)
                throw new DetourException("target method not found");
            if (detour == null)
                throw new DetourException("detour method not found");
            CheckSignatures(target, detour);

            targetCode = NativeCode(target);
            IntPtr detourCode = NativeCode(detour);
            Log.Debug($"Hooking {Describe(target)}: entry {Bytes(target.MethodHandle.GetFunctionPointer())}, " +
                      $"code 0x{targetCode.ToInt64():X} {Bytes(targetCode)}");
            int status = NativeApi.CreateHook(targetCode, detourCode, out IntPtr original);
            if (status != 0 || original == IntPtr.Zero)
                throw new DetourException($"MinHook status {status} for {Describe(target)} at 0x{targetCode.ToInt64():X}");
            return original;
        }

        private static string Bytes(IntPtr address)
        {
            var text = new System.Text.StringBuilder(64);
            byte* bytes = (byte*)address;
            for (int i = 0; i < 16; i++)
                text.Append(bytes[i].ToString("X2"));
            return text.ToString();
        }

        public static string Describe(MethodBase method) =>
            method == null ? "<null>" : method.DeclaringType?.FullName + "::" + method.Name;

        private static void CheckSignatures(MethodBase target, MethodBase detour)
        {
            if (!detour.IsStatic)
                throw new DetourException($"detour {Describe(detour)} must be static");
            ParameterInfo[] targetParameters = target.GetParameters();
            ParameterInfo[] detourParameters = detour.GetParameters();
            int offset = target.IsStatic ? 0 : 1;
            if (detourParameters.Length != targetParameters.Length + offset)
                throw new DetourException($"parameter count mismatch for {Describe(target)}");
            if (!target.IsStatic && detourParameters[0].ParameterType.IsValueType)
                throw new DetourException($"'this' of {Describe(target)} must be a reference");
            for (int i = 0; i < targetParameters.Length; i++)
            {
                Type expected = targetParameters[i].ParameterType;
                Type actual = detourParameters[i + offset].ParameterType;
                // Internal game types are passed as object (same register,
                // same GC reference) where the detour cannot name them.
                if (expected != actual && !(actual == typeof(object) && !expected.IsValueType && !expected.IsByRef))
                    throw new DetourException($"parameter {i} of {Describe(target)}: {expected} vs {actual}");
            }
            Type targetReturn = (target as MethodInfo)?.ReturnType ?? typeof(void);
            Type detourReturn = (detour as MethodInfo)?.ReturnType ?? typeof(void);
            if (targetReturn != detourReturn)
                throw new DetourException($"return type mismatch for {Describe(target)}");
        }

        // Proves the whole path (precode following, MinHook on JIT code, the
        // trampoline called through a function pointer) on methods of our own
        // before any game method is touched.
        public static string SelfTest()
        {
            try
            {
                MethodInfo target = typeof(Detour).GetMethod(nameof(SelfTestTarget), BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo detour = typeof(Detour).GetMethod(nameof(SelfTestDetour), BindingFlags.NonPublic | BindingFlags.Static);
                s_selfTestOriginal = (delegate*<int, int>)Install(target, detour);
                int result = SelfTestTarget(4);
                return result == 50 ? null : $"self-test returned {result}, expected 50";
            }
            catch (Exception exception)
            {
                return "self-test failed: " + exception.Message;
            }
        }

        private static delegate*<int, int> s_selfTestOriginal;

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private static int SelfTestTarget(int value) => value + 1;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int SelfTestDetour(int value) => s_selfTestOriginal(value) * 10;
    }
}
