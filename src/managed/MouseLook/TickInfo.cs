using VRageMath;

namespace SpaceEngineers.HighFPSFix.MouseLook
{
    // What the render thread needs to know about a simulation step to lead the
    // camera with the mouse, recorded on the update thread at the end of the
    // step and matched to the step's frame by its timestamp.
    internal struct TickInfo
    {
        // The frame's UpdateTimestamp (ticks of the game timer).
        public long Timestamp;
        // Cumulative mouse counts the game had received by this step.
        public long CountsX;
        public long CountsY;
        // The player looks through their own character, in first person, with
        // the gameplay screen focused and the game running.
        public bool FirstPerson;
        // The character's up axis (yaw turns about it).
        public Vector3D Up;
        // Render objects that turn with the view: the character, then its
        // held tool and the tool's subparts.
        public uint[] Owners;
    }
}
