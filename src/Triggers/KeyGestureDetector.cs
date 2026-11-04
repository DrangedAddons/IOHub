namespace IOHub.Triggers;

/// <summary>
/// Turns a stream of key downs and ups into hotkey, tap and hold gestures. Pure logic with no Windows calls, so
/// it can be driven by the keyboard hook or by a test. Times are milliseconds from any fixed start.
/// </summary>
public sealed class KeyGestureDetector
{
    public sealed record Entry(TriggerKind Kind, int Key, KeyMods Mods, GestureKind Gesture, int HoldMs, object Tag);

    /// <summary>Longest press that still counts as a tap.</summary>
    public int TapMaxMs { get; set; } = 300;
    /// <summary>Longest gap between one tap's release and the next press.</summary>
    public int TapGapMs { get; set; } = 350;

    readonly List<Entry> _entries = [];
    readonly HashSet<int> _down = [];
    readonly HashSet<int> _swallowed = [];

    // Tap and hold state for the key currently being watched.
    readonly Dictionary<int, long> _downAt = [];
    readonly HashSet<int> _used = [];        // another key went down while this one was held: not a tap or hold
    readonly HashSet<int> _holdFired = [];
    int _tapKey;
    int _tapCount;
    long _lastTapUp;
    long _pendingDeadline = -1;               // waiting to see if a double tap becomes a triple tap
    readonly Dictionary<int, List<object>> _whileHeld = []; // keys held down for "while held" triggers, and those triggers

    public event Action<object>? Fired;
    /// <summary>A "while held" trigger's key was let go.</summary>
    public event Action<object>? Released;

    public void SetEntries(IEnumerable<Entry> entries)
    {
        _entries.Clear();
        _entries.AddRange(entries);
        ResetTaps();
    }

    public bool HasEntries => _entries.Count > 0;

    KeyMods HeldMods(int except)
    {
        var mods = KeyMods.None;
        foreach (var k in _down) if (k != except) mods |= KeyNames.ModOf(k);
        return mods;
    }

    /// <summary>Feeds one key event. Returns true when the key should be kept from other apps.</summary>
    public bool OnKey(int vk, bool isDown, long now)
    {
        if (isDown)
        {
            bool repeat = !_down.Add(vk);
            if (repeat) return _swallowed.Contains(vk);

            // A combo fires on the main key's press and keeps that press (and its release) from other apps.
            var mods = HeldMods(vk);
            foreach (var e in _entries)
            {
                if (e.Kind != TriggerKind.Hotkey || e.Key != vk || e.Mods != mods) continue;
                _swallowed.Add(vk);
                ResetTaps();
                Fired?.Invoke(e.Tag);
                return true;
            }

            // "While held" starts on the press and ends on the release (below).
            if (mods == KeyMods.None)
                foreach (var e in _entries)
                {
                    if (e.Kind == TriggerKind.Hotkey || e.Gesture != GestureKind.WhileHeld || e.Key != vk) continue;
                    if (!_whileHeld.TryGetValue(vk, out var tags)) _whileHeld[vk] = tags = [];
                    tags.Add(e.Tag);
                    Fired?.Invoke(e.Tag);
                }

            // Pressing anything marks keys already held as "used" (Ctrl in Ctrl+C isn't a tap of Ctrl).
            foreach (var held in _down) if (held != vk) _used.Add(held);
            _downAt[vk] = now;
            _used.Remove(vk);
            _holdFired.Remove(vk);
            if (mods != KeyMods.None) _used.Add(vk); // Shift+A isn't a tap of A
            if (vk != _tapKey) ResetTaps();
            _pendingDeadline = -1; // a new press continues the tap run instead of settling it
            return false;
        }

        _down.Remove(vk);
        if (_whileHeld.Remove(vk, out var heldTags)) foreach (var tag in heldTags) Released?.Invoke(tag);
        if (_swallowed.Remove(vk)) return true;

        bool used = _used.Remove(vk);
        if (!_downAt.Remove(vk, out var downAt) || used || _holdFired.Remove(vk)) { ResetTaps(); return false; }
        if (now - downAt > TapMaxMs) { ResetTaps(); return false; }

        // A tap.
        if (vk == _tapKey && _tapCount > 0 && downAt - _lastTapUp <= TapGapMs) _tapCount++;
        else { _tapKey = vk; _tapCount = 1; }
        _lastTapUp = now;

        var exact = TapEntry(vk, _tapCount);
        bool more = _entries.Any(e => e.Key == vk && IsTap(e) && TapCount(e) > _tapCount);
        if (exact != null && !more) { ResetTaps(); Fired?.Invoke(exact.Tag); }
        else if (exact != null) _pendingDeadline = now + TapGapMs; // wait: it might become a triple
        else if (!more) ResetTaps();
        return false;
    }

    /// <summary>Call regularly (every ~25 ms). Fires holds and settled taps.</summary>
    public void Tick(long now)
    {
        if (_pendingDeadline >= 0 && now >= _pendingDeadline)
        {
            var exact = TapEntry(_tapKey, _tapCount);
            ResetTaps();
            if (exact != null) Fired?.Invoke(exact.Tag);
        }

        foreach (var (vk, at) in _downAt)
        {
            if (_used.Contains(vk) || _holdFired.Contains(vk)) continue;
            foreach (var e in _entries)
            {
                if (!IsHold(e) || e.Key != vk || now - at < e.HoldMs) continue;
                _holdFired.Add(vk);
                Fired?.Invoke(e.Tag);
                break;
            }
        }
    }

    /// <summary>Forget held keys, e.g. after the hook was paused; a release we never saw would leave a key stuck.</summary>
    public void Reset()
    {
        var held = _whileHeld.Values.SelectMany(t => t).ToList();
        _whileHeld.Clear();
        foreach (var tag in held) Released?.Invoke(tag);
        _down.Clear();
        _swallowed.Clear();
        _downAt.Clear();
        _used.Clear();
        _holdFired.Clear();
        ResetTaps();
    }

    void ResetTaps()
    {
        _tapKey = 0;
        _tapCount = 0;
        _pendingDeadline = -1;
    }

    Entry? TapEntry(int vk, int count) => _entries.FirstOrDefault(e => e.Key == vk && IsTap(e) && TapCount(e) == count);
    static bool IsTap(Entry e) => e.Kind != TriggerKind.Hotkey && e.Gesture is GestureKind.DoubleTap or GestureKind.TripleTap;
    static bool IsHold(Entry e) => e.Kind != TriggerKind.Hotkey && e.Gesture == GestureKind.Hold;
    static int TapCount(Entry e) => e.Gesture == GestureKind.TripleTap ? 3 : 2;
}
