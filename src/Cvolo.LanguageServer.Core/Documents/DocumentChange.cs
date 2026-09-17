namespace Cvolo.LanguageServer.Core.Documents;

/// <summary>
/// One document change from a didChange notification. A null <see cref="Range"/>
/// means the whole document is replaced by <see cref="Text"/>.
/// </summary>
internal readonly record struct DocumentChange(TextRange? Range, string Text);