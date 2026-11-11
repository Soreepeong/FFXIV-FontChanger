namespace FontChanger.DalamudPlugin.Fonts.Records;

/// <summary>What reading a request's sources came to.</summary>
/// <param name="Status">The status line.</param>
/// <param name="Watch">The files to watch from now on, null to keep watching the same.</param>
internal sealed record LoadReport(string Status, WatchList? Watch);
