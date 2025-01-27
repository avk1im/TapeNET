using System.Collections.ObjectModel;

namespace TapeLibNET
{

    public enum TapeState
    {
        NotInitialized,
        Open,
        MediaLoaded,
        MediaPrepared,
        ReadingTOC,
        WritingTOC,
        ReadingContent,
        WritingContent,
    }

    // A simple state machine that tries to foolproof TapeManager usage a bit
    //  by enforcing valid state transitions, e.g. properly finishing writing operations before reading
    public class TapeManagerState(TapeState initState = TapeState.NotInitialized)
    {
        private readonly Dictionary<TapeState, ReadOnlyCollection<TapeState>> m_validTransitions = new()
        {
            [TapeState.NotInitialized] = new([TapeState.Open]),
            [TapeState.Open] = new([TapeState.NotInitialized, TapeState.MediaLoaded]),
            [TapeState.MediaLoaded] = new([TapeState.NotInitialized, TapeState.Open, TapeState.MediaPrepared]),
            [TapeState.MediaPrepared] = new([TapeState.Open, TapeState.MediaLoaded,
                TapeState.ReadingTOC, TapeState.WritingTOC, TapeState.ReadingContent, TapeState.WritingContent]),
            [TapeState.ReadingTOC] = new([TapeState.MediaPrepared, TapeState.WritingTOC, TapeState.ReadingContent, TapeState.WritingContent]),
            [TapeState.WritingTOC] = new([TapeState.MediaPrepared]),
            [TapeState.ReadingContent] = new([TapeState.MediaPrepared, TapeState.ReadingTOC, TapeState.WritingTOC, TapeState.WritingContent]),
            [TapeState.WritingContent] = new([TapeState.MediaPrepared]),
        };

        public TapeState CurrentState { get; private set; } = initState;

        public override string? ToString() => CurrentState.ToString();

        //public override bool Equals(object? obj) => (obj is TapeManagerState ts && CurrentState == ts.CurrentState) || (obj is TapeState state && CurrentState == state);
        //public override int GetHashCode() => (int)CurrentState;
        //public static bool operator == (TapeManagerState ts, TapeState state) => ts.CurrentState == state;
        //public static bool operator != (TapeManagerState ts, TapeState state) => ts.CurrentState != state;
        // the below implicit conversion covers all the functionality needed to compare vs. TapeState literals
        public static implicit operator TapeState(TapeManagerState ts) => ts.CurrentState;

        public bool IsOneOf(params TapeState[] states)
        {
            return states.Contains(CurrentState);
        }

        public bool CanTransitionTo(TapeState nextState)
        {
            if (nextState == CurrentState)
                return true;

            return m_validTransitions.TryGetValue(CurrentState, out var allowedTransitions) &&
                   allowedTransitions.Contains(nextState);
        }

        public void TransitionTo(TapeState nextState)
        {
            if (!CanTransitionTo(nextState))
                throw new InvalidOperationException($"Invalid state transition from {CurrentState} to {nextState}");

            CurrentState = nextState;
        }

        public bool TryTransitionTo(TapeState nextState)
        {
            if (!CanTransitionTo(nextState))
            {
                return false;
            }

            CurrentState = nextState;
            return true;
        }

        internal void Reset()
        {
            CurrentState = TapeState.NotInitialized;
        }

    } // class TapeManagerState

} // namespace TapeNET