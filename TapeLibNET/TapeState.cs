using System.Collections.ObjectModel;

namespace TapeLibNET
{

    /// <summary>
    /// Lifecycle states of a <see cref="TapeStreamManager"/>.
    /// <code>
    /// NotInitialized → Open → MediaLoaded → MediaPrepared ⇄ Reading/WritingTOC/Content
    /// </code>
    /// </summary>
    public enum TapeState
    {
        /// <summary>Drive not yet opened.</summary>
        NotInitialized,
        /// <summary>Drive handle acquired, no media loaded.</summary>
        Open,
        /// <summary>Media detected in drive.</summary>
        MediaLoaded,
        /// <summary>Media formatted and ready for I/O operations.</summary>
        MediaPrepared,
        /// <summary>Reading from the TOC area.</summary>
        ReadingTOC,
        /// <summary>Writing to the TOC area.</summary>
        WritingTOC,
        /// <summary>Reading from a content set.</summary>
        ReadingContent,
        /// <summary>Writing to a content set.</summary>
        WritingContent,
    }

    /// <summary>
    /// Dictionary-based state machine enforcing valid <see cref="TapeState"/> transitions.
    /// <para>Implicitly converts to <see cref="TapeState"/> for convenient comparison with state literals.</para>
    /// </summary>
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

        /// <summary>Current state of the machine.</summary>
        public TapeState CurrentState { get; private set; } = initState;

        /// <inheritdoc />
        public override string? ToString() => CurrentState.ToString();

        //public override bool Equals(object? obj) => (obj is TapeManagerState ts && CurrentState == ts.CurrentState) || (obj is TapeState state && CurrentState == state);
        //public override int GetHashCode() => (int)CurrentState;
        //public static bool operator == (TapeManagerState ts, TapeState state) => ts.CurrentState == state;
        //public static bool operator != (TapeManagerState ts, TapeState state) => ts.CurrentState != state;
        /// <summary>Implicit conversion to <see cref="TapeState"/> for direct comparison.</summary>
        public static implicit operator TapeState(TapeManagerState ts) => ts.CurrentState;

        /// <summary>Returns <see langword="true"/> if <see cref="CurrentState"/> matches any of the given <paramref name="states"/>.</summary>
        public bool IsOneOf(params TapeState[] states)
        {
            return states.Contains(CurrentState);
        }

        /// <summary>Checks whether transitioning to <paramref name="nextState"/> is allowed.</summary>
        public bool CanTransitionTo(TapeState nextState)
        {
            if (nextState == CurrentState)
                return true;

            return m_validTransitions.TryGetValue(CurrentState, out var allowedTransitions) &&
                   allowedTransitions.Contains(nextState);
        }

        /// <summary>Transitions to <paramref name="nextState"/>; throws <see cref="InvalidOperationException"/> if invalid.</summary>
        public void TransitionTo(TapeState nextState)
        {
            if (!CanTransitionTo(nextState))
                throw new InvalidOperationException($"Invalid state transition from {CurrentState} to {nextState}");

            CurrentState = nextState;
        }

        /// <summary>Attempts transition to <paramref name="nextState"/>; returns <see langword="false"/> if invalid.</summary>
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