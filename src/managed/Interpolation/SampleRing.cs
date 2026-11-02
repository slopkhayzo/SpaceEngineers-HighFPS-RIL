namespace SpaceEngineers.HighFPSFix.Interpolation
{
    // The last few samples of something the simulation updates, tagged with
    // the sequence number of the frame that carried them. The value at frame
    // n is the newest sample with a sequence number <= n.
    internal sealed class SampleRing<T>
    {
        public const int Capacity = 4;

        private readonly long[] m_seq = new long[Capacity];
        private readonly T[] m_value = new T[Capacity];
        // True when the sample must not be blended into from its predecessor.
        private readonly bool[] m_cut = new bool[Capacity];
        private int m_head = -1;

        public int Count { get; private set; }

        public long LatestSeq => Count == 0 ? long.MinValue : m_seq[m_head];

        public ref T Latest => ref m_value[m_head];

        public void Clear()
        {
            Count = 0;
            m_head = -1;
        }

        // Adds (or, within the same frame, replaces) the newest sample.
        public ref T Push(long seq, bool cut)
        {
            if (Count > 0 && m_seq[m_head] == seq)
            {
                m_cut[m_head] |= cut;
                return ref m_value[m_head];
            }
            m_head = (m_head + 1) % Capacity;
            m_seq[m_head] = seq;
            m_cut[m_head] = cut || Count == 0;
            if (Count < Capacity)
                Count++;
            return ref m_value[m_head];
        }

        // Index of the newest sample at or before seq, or -1.
        public int IndexAt(long seq)
        {
            for (int i = 0; i < Count; i++)
            {
                int index = (m_head - i + Capacity) % Capacity;
                if (m_seq[index] <= seq)
                    return index;
            }
            return -1;
        }

        public int OldestIndex => Count == 0 ? -1 : (m_head - Count + 1 + Capacity) % Capacity;

        public ref T At(int index) => ref m_value[index];

        public long SeqAt(int index) => m_seq[index];

        // Whether any sample after 'from' up to and including 'to' is a cut.
        public bool CutBetween(int from, int to)
        {
            if (from == to)
                return false;
            for (int index = to; index != from; index = (index - 1 + Capacity) % Capacity)
            {
                if (m_cut[index])
                    return true;
            }
            return false;
        }
    }
}
