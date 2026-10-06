namespace TypePilot.Core;

public enum SuggestionKeyAction { None, Next, Dismiss, CancelPending }
public readonly record struct SuggestionKeyDecision(bool Consume, SuggestionKeyAction Action);

public sealed class SuggestionKeyPolicy
{
    private bool _tabDown, _escapeDown;
    public void Reset() => _tabDown = _escapeDown = false;
    public SuggestionKeyDecision Route(uint key, bool down, bool up, bool injected, bool target, bool modifiersReleased)
    {
        if (injected) return new(false, SuggestionKeyAction.None);
        if (up && key == 9 && _tabDown) { _tabDown = false; return new(true, SuggestionKeyAction.None); }
        if (up && key == 27 && _escapeDown) { _escapeDown = false; return new(true, SuggestionKeyAction.None); }
        if (down && target && modifiersReleased && key == 9)
        {
            var action = _tabDown ? SuggestionKeyAction.None : SuggestionKeyAction.Next;
            _tabDown = true; return new(true, action);
        }
        if (down && target && modifiersReleased && key == 27)
        { _escapeDown = true; return new(true, SuggestionKeyAction.Dismiss); }
        return new(false, down ? SuggestionKeyAction.CancelPending : SuggestionKeyAction.None);
    }
}
