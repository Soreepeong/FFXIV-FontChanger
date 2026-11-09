using System;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>The frame and tick that caches and atlas planes are aged by.</summary>
internal sealed class FrameClock
{
    /// <summary>Gets the frame, advanced at the end of each Present.</summary>
    public long Frame { get; private set; }

    /// <summary>Gets the tick at the start of the last Present, which glyphs drawn until the next are marked with.</summary>
    public long Tick { get; private set; }

    public void BeginPresent() => this.Tick = Environment.TickCount64;

    public void EndFrame() => this.Frame++;
}
