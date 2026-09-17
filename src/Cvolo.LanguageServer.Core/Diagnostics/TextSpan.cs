namespace Cvolo.LanguageServer.Core.Diagnostics;

/// <summary>
/// Half-open span over a document's UTF-16 code units: <see cref="Start"/> is
/// the absolute offset of the first code unit and <see cref="End"/> is one past
/// the last. Backend-neutral: no compiler or protocol types are involved.
/// </summary>
internal readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}
