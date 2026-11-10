namespace IOHub.Triggers;

/// <summary>The non-Xbox controllers IO Hub reads itself, over HID.</summary>
public enum PadModel { DualShock4, DualSense, SwitchPro, JoyConLeft, JoyConRight }

/// <summary>
/// The report formats of PlayStation and Switch controllers: input reports turned into the same buttons and sticks an
/// Xbox controller gives, and the output reports that rumble them. Pure byte work, so a test can feed it captured
/// reports. Offsets follow Linux's hid-playstation and hid-nintendo drivers.
///
/// Buttons map by position, the way Steam does it: the bottom face button is A on every controller (Cross on
/// PlayStation, B on Switch), the right one is B, and so on. Share/Create/Minus are View, Options/Plus are Menu, and
/// the PS and Home buttons are the Xbox button.
/// </summary>
public static class PadReports
{
    public static PadModel? ModelOf(ushort vid, ushort pid) => (vid, pid) switch
    {
        (0x054C, 0x05C4 or 0x09CC or 0x0BA0) => PadModel.DualShock4, // v1, v2, Sony's USB wireless adapter
        (0x054C, 0x0CE6 or 0x0DF2) => PadModel.DualSense,             // DualSense, DualSense Edge
        (0x057E, 0x2009) => PadModel.SwitchPro,                        // also what most third-party Pro pads report
        (0x057E, 0x2006) => PadModel.JoyConLeft,
        (0x057E, 0x2007) => PadModel.JoyConRight,
        _ => null,
    };

    public static string NameOf(PadModel m) => m switch
    {
        PadModel.DualShock4 => "DualShock 4",
        PadModel.DualSense => "DualSense",
        PadModel.SwitchPro => "Switch Pro Controller",
        PadModel.JoyConLeft => "Joy-Con (L)",
        PadModel.JoyConRight => "Joy-Con (R)",
        _ => "Controller",
    };

    public static bool IsSwitch(PadModel m) => m is PadModel.SwitchPro or PadModel.JoyConLeft or PadModel.JoyConRight;

    /// <summary>What one input report said. Battery is 0 empty … 3 full like XInput's, or -1 when the report has none.</summary>
    public readonly record struct Reading(XInputPad.Pad Pad, int Battery);

    // ---------------- PlayStation ----------------

    /// <summary>
    /// DualShock 4. USB sends report 0x01 (64 bytes). Bluetooth starts with a short 0x01 (sticks and buttons only) and
    /// sends 0x11 (data two bytes later, plus battery) once something has written to it.
    /// </summary>
    public static bool TryParseDualShock4(ReadOnlySpan<byte> r, bool bluetooth, out Reading reading)
    {
        reading = default;
        int o;
        if (r.Length >= 10 && r[0] == 0x01) o = 1;
        else if (r.Length >= 33 && r[0] == 0x11) o = 3;
        else return false;

        // Sticks at o..o+3, buttons o+4..o+6, L2/R2 o+7, o+8.
        int buttons = PlayStationButtons(r[o + 4], r[o + 5], r[o + 6], hasMute: false);
        if (r[o + 7] > 127) buttons |= ControllerButtons.LT;
        if (r[o + 8] > 127) buttons |= ControllerButtons.RT;
        var pad = new XInputPad.Pad(buttons, Axis8(r[o]), Axis8(r[o + 1], invert: true), Axis8(r[o + 2]), Axis8(r[o + 3], invert: true));

        // Status byte: low nibble 0-10 (11 = full on cable), bit 4 = cable. Only the long reports have it, and only
        // Bluetooth runs on battery.
        int battery = -1;
        if (bluetooth && o == 3)
        {
            int level = r[o + 29] & 0x0F;
            battery = Level(level >= 10 ? 100 : level * 10 + 5);
        }
        reading = new Reading(pad, battery);
        return true;
    }

    /// <summary>
    /// DualSense. USB sends report 0x01 (64 bytes). Bluetooth starts with a short DualShock-like 0x01 and sends 0x31
    /// (data one byte later than USB) once its calibration has been read.
    /// </summary>
    public static bool TryParseDualSense(ReadOnlySpan<byte> r, bool bluetooth, out Reading reading)
    {
        reading = default;
        if (bluetooth && r.Length >= 10 && r[0] == 0x01)
        {
            // The short Bluetooth report has the DualShock 4 layout.
            int b = PlayStationButtons(r[5], r[6], r[7], hasMute: false);
            if (r[8] > 127) b |= ControllerButtons.LT;
            if (r[9] > 127) b |= ControllerButtons.RT;
            reading = new Reading(new XInputPad.Pad(b, Axis8(r[1]), Axis8(r[2], invert: true), Axis8(r[3]), Axis8(r[4], invert: true)), -1);
            return true;
        }

        int o;
        if (!bluetooth && r.Length >= 54 && r[0] == 0x01) o = 1;
        else if (bluetooth && r.Length >= 55 && r[0] == 0x31) o = 2;
        else return false;

        // Sticks o..o+3, L2/R2 o+4/o+5, a counter, buttons o+7..o+9, status o+52.
        int buttons = PlayStationButtons(r[o + 7], r[o + 8], r[o + 9], hasMute: true);
        if (r[o + 4] > 127) buttons |= ControllerButtons.LT;
        if (r[o + 5] > 127) buttons |= ControllerButtons.RT;
        var pad = new XInputPad.Pad(buttons, Axis8(r[o]), Axis8(r[o + 1], invert: true), Axis8(r[o + 2]), Axis8(r[o + 3], invert: true));

        int battery = -1;
        if (bluetooth)
        {
            int level = r[o + 52] & 0x0F; // 0-10; the high nibble is charging state
            battery = Level(Math.Min(100, level * 10 + 5));
        }
        reading = new Reading(pad, battery);
        return true;
    }

    /// <summary>The three button bytes both PlayStation controllers share: hat and face, shoulders and sticks, PS.</summary>
    static int PlayStationButtons(byte face, byte shoulders, byte system, bool hasMute)
    {
        int b = Hat(face & 0x0F);
        if ((face & 0x10) != 0) b |= ControllerButtons.X;  // Square
        if ((face & 0x20) != 0) b |= ControllerButtons.A;  // Cross
        if ((face & 0x40) != 0) b |= ControllerButtons.B;  // Circle
        if ((face & 0x80) != 0) b |= ControllerButtons.Y;  // Triangle
        if ((shoulders & 0x01) != 0) b |= ControllerButtons.LB;
        if ((shoulders & 0x02) != 0) b |= ControllerButtons.RB;
        if ((shoulders & 0x10) != 0) b |= ControllerButtons.View;  // Share / Create
        if ((shoulders & 0x20) != 0) b |= ControllerButtons.Menu;  // Options
        if ((shoulders & 0x40) != 0) b |= ControllerButtons.LeftStick;
        if ((shoulders & 0x80) != 0) b |= ControllerButtons.RightStick;
        if ((system & 0x01) != 0) b |= ControllerButtons.Guide;    // PS
        if ((system & 0x02) != 0) b |= ControllerButtons.Touchpad; // touchpad click
        // On the DualShock 4 the rest of this byte is a counter; on the DualSense bit 2 is the mic button.
        if (hasMute && (system & 0x04) != 0) b |= ControllerButtons.Mute;
        return b;
    }

    /// <summary>A hat switch: 0 up, then clockwise in eighths; 8 and above is centred.</summary>
    static int Hat(int hat) => hat switch
    {
        0 => ControllerButtons.DPadUp,
        1 => ControllerButtons.DPadUp | ControllerButtons.DPadRight,
        2 => ControllerButtons.DPadRight,
        3 => ControllerButtons.DPadDown | ControllerButtons.DPadRight,
        4 => ControllerButtons.DPadDown,
        5 => ControllerButtons.DPadDown | ControllerButtons.DPadLeft,
        6 => ControllerButtons.DPadLeft,
        7 => ControllerButtons.DPadUp | ControllerButtons.DPadLeft,
        _ => 0,
    };

    /// <summary>An 8-bit axis (0-255, 128 centre) as XInput's -32768..32767. PlayStation sticks read 0 at the top, so Y inverts.</summary>
    static short Axis8(byte v, bool invert = false)
    {
        int x = v * 257 - 32767;
        if (invert) x = -x;
        return (short)Math.Clamp(x, -32767, 32767);
    }

    static int Level(int percent) => percent switch { < 10 => 0, < 40 => 1, < 70 => 2, _ => 3 };

    /// <summary>DualShock 4 rumble: strong is the left (big) motor, weak the right.</summary>
    public static byte[] DualShock4Rumble(bool bluetooth, byte strong, byte weak)
    {
        if (!bluetooth)
        {
            var usb = new byte[32];
            usb[0] = 0x05;
            usb[1] = 0x01; // only the motors change; the light bar is left alone
            usb[4] = weak;
            usb[5] = strong;
            return usb;
        }
        var bt = new byte[78];
        bt[0] = 0x11;
        bt[1] = 0xC0; // HID report with a CRC
        bt[3] = 0x01;
        bt[6] = weak;
        bt[7] = strong;
        AddCrc(bt);
        return bt;
    }

    /// <summary>DualSense rumble in its "compatible vibration" mode, which drives the two motors like a DualShock 4.</summary>
    public static byte[] DualSenseRumble(bool bluetooth, int sequence, byte strong, byte weak)
    {
        if (!bluetooth)
        {
            var usb = new byte[63];
            usb[0] = 0x02;
            usb[1] = 0x03; // compatible vibration + haptics select
            usb[3] = weak;
            usb[4] = strong;
            return usb;
        }
        var bt = new byte[78];
        bt[0] = 0x31;
        bt[1] = (byte)((sequence & 0x0F) << 4);
        bt[2] = 0x10; // the tag it needs
        bt[3] = 0x03;
        bt[5] = weak;
        bt[6] = strong;
        AddCrc(bt);
        return bt;
    }

    /// <summary>Bluetooth output reports end in a CRC-32 of 0xA2 (the HID output header) and everything before it.</summary>
    static void AddCrc(byte[] report)
    {
        uint crc = Crc32(0xFFFFFFFF, 0xA2);
        for (int i = 0; i < report.Length - 4; i++) crc = Crc32(crc, report[i]);
        crc = ~crc;
        report[^4] = (byte)crc;
        report[^3] = (byte)(crc >> 8);
        report[^2] = (byte)(crc >> 16);
        report[^1] = (byte)(crc >> 24);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(uint crc, byte b) => CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

    /// <summary>The standard CRC-32 of some bytes, for tests.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) crc = Crc32(crc, b);
        return ~crc;
    }

    // ---------------- Switch ----------------

    /// <summary>
    /// Switch sticks are 12-bit and their centre differs from pad to pad. The first reading near the middle is taken
    /// as centre, and the range grows to the furthest each direction has been pushed (never less than a typical pad's).
    /// </summary>
    public sealed class StickCalibration
    {
        int _cx = -1, _cy;
        int _right = 1400, _left = 1400, _up = 1400, _down = 1400;

        public (short X, short Y) Map(int x, int y)
        {
            if (_cx < 0)
            {
                bool nearMiddle = Math.Abs(x - 2048) < 500 && Math.Abs(y - 2048) < 500;
                _cx = nearMiddle ? x : 2048;
                _cy = nearMiddle ? y : 2048;
            }
            int dx = x - _cx, dy = y - _cy;
            if (dx > _right) _right = dx; else if (-dx > _left) _left = -dx;
            if (dy > _up) _up = dy; else if (-dy > _down) _down = -dy;
            return (Scale(dx, dx >= 0 ? _right : _left), Scale(dy, dy >= 0 ? _up : _down));
        }

        static short Scale(int d, int range) => (short)Math.Clamp(d * 32767 / range, -32767, 32767);
    }

    /// <summary>
    /// Report 0x30 (full mode), and 0x21 (a reply to a command, which starts with the same state). Joy-Cons fill in
    /// only their own half: the left one its stick, D-pad, L, ZL and Minus.
    /// </summary>
    public static bool TryParseSwitch(ReadOnlySpan<byte> r, PadModel model, StickCalibration left, StickCalibration right, out Reading reading)
    {
        reading = default;
        if (r.Length < 12 || (r[0] != 0x30 && r[0] != 0x21)) return false;
        byte rb = r[3], shared = r[4], lb = r[5];

        int b = 0;
        // By position: Switch's bottom button is B, which is Xbox's A.
        if ((rb & 0x04) != 0) b |= ControllerButtons.A;  // B
        if ((rb & 0x08) != 0) b |= ControllerButtons.B;  // A
        if ((rb & 0x01) != 0) b |= ControllerButtons.X;  // Y
        if ((rb & 0x02) != 0) b |= ControllerButtons.Y;  // X
        if ((rb & 0x40) != 0) b |= ControllerButtons.RB; // R
        if ((rb & 0x80) != 0) b |= ControllerButtons.RT; // ZR
        if ((shared & 0x01) != 0) b |= ControllerButtons.View;  // Minus
        if ((shared & 0x02) != 0) b |= ControllerButtons.Menu;  // Plus
        if ((shared & 0x04) != 0) b |= ControllerButtons.RightStick;
        if ((shared & 0x08) != 0) b |= ControllerButtons.LeftStick;
        if ((shared & 0x10) != 0) b |= ControllerButtons.Guide; // Home
        if ((shared & 0x20) != 0) b |= ControllerButtons.Capture;
        if ((lb & 0x01) != 0) b |= ControllerButtons.DPadDown;
        if ((lb & 0x02) != 0) b |= ControllerButtons.DPadUp;
        if ((lb & 0x04) != 0) b |= ControllerButtons.DPadRight;
        if ((lb & 0x08) != 0) b |= ControllerButtons.DPadLeft;
        if ((lb & 0x40) != 0) b |= ControllerButtons.LB; // L
        if ((lb & 0x80) != 0) b |= ControllerButtons.LT; // ZL

        short lx = 0, ly = 0, rx = 0, ry = 0;
        if (model != PadModel.JoyConRight)
            (lx, ly) = left.Map(r[6] | ((r[7] & 0x0F) << 8), (r[7] >> 4) | (r[8] << 4));
        if (model != PadModel.JoyConLeft)
            (rx, ry) = right.Map(r[9] | ((r[10] & 0x0F) << 8), (r[10] >> 4) | (r[11] << 4));

        // High nibble of byte 2: level in its top three bits (0 empty … 4 full), charging in the lowest.
        int level = (r[2] >> 5) & 0x07;
        int battery = level switch { 0 => 0, 1 or 2 => 1, 3 => 2, _ => 3 };
        reading = new Reading(new XInputPad.Pad(b, lx, ly, rx, ry), battery);
        return true;
    }

    /// <summary>Report 0x10: rumble only. <paramref name="amplitude"/> is 0-1 for both sides.</summary>
    public static byte[] SwitchRumble(int counter, double amplitude)
    {
        var r = new byte[10];
        r[0] = 0x10;
        r[1] = (byte)(counter & 0x0F);
        EncodeSwitchRumble(r.AsSpan(2, 4), amplitude);
        EncodeSwitchRumble(r.AsSpan(6, 4), amplitude);
        return r;
    }

    /// <summary>Report 0x01: a command (sub-command id and its arguments), with the rumble bytes at rest.</summary>
    public static byte[] SwitchCommand(int counter, byte command, params byte[] args)
    {
        var r = new byte[11 + args.Length];
        r[0] = 0x01;
        r[1] = (byte)(counter & 0x0F);
        EncodeSwitchRumble(r.AsSpan(2, 4), 0);
        EncodeSwitchRumble(r.AsSpan(6, 4), 0);
        r[10] = command;
        args.CopyTo(r, 11);
        return r;
    }

    /// <summary>
    /// One side of Switch HD rumble: a 320 Hz high band and 160 Hz low band, both at <paramref name="amplitude"/>.
    /// 0 is the at-rest pattern 00 01 40 40. The amplitude curve is the one the open-source Joy-Con drivers use.
    /// </summary>
    public static void EncodeSwitchRumble(Span<byte> d, double amplitude)
    {
        const int hf = (160 - 0x60) * 4; // 320 Hz: round(log2(320 / 10) * 32) = 160
        const int lf = 128 - 0x40;       // 160 Hz: round(log2(160 / 10) * 32) = 128
        int a = 0;
        if (amplitude > 0.23) a = (int)Math.Round(Math.Log2(Math.Min(amplitude, 1.0) * 8.7) * 32);
        else if (amplitude * 17 > 1) a = (int)Math.Round(Math.Log2(amplitude * 17) * 16);
        a = Math.Clamp(a, 0, 100);
        int hfAmp = a * 2, lfAmp = a / 2 + 64;
        d[0] = (byte)(hf & 0xFF);
        d[1] = (byte)(hfAmp + (hf >> 8));
        d[2] = (byte)(lf + (lfAmp >> 8));
        d[3] = (byte)(lfAmp & 0xFF);
    }

    /// <summary>The sub-command a 0x21 reply answers, or -1.</summary>
    public static int SwitchReplyTo(ReadOnlySpan<byte> r) => r.Length > 14 && r[0] == 0x21 ? r[14] : -1;
}
