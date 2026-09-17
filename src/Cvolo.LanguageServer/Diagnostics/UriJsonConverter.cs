using Newtonsoft.Json;

namespace Cvolo.LanguageServer.Diagnostics;

/// <summary>
/// Serializes <see cref="Uri"/> values using their canonical absolute form.
/// Newtonsoft's default <see cref="Uri"/> handling emits
/// <see cref="Uri.OriginalString"/>, which for a Windows drive path such as
/// <c>d:\dir\file.cvl</c> produces a URI with scheme <c>d</c> instead of
/// <c>file:///d:/dir/file.cvl</c>. Clients such as VS Code then fail to resolve
/// the resource, so diagnostics cannot be associated with the open document.
/// </summary>
internal sealed class UriJsonConverter : JsonConverter<Uri>
{
    public override Uri? ReadJson(JsonReader reader, Type objectType, Uri? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        return reader.Value is string value && value.Length > 0
            ? new Uri(value, UriKind.RelativeOrAbsolute)
            : null;
    }

    public override void WriteJson(JsonWriter writer, Uri? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteValue(value.IsAbsoluteUri ? value.AbsoluteUri : value.OriginalString);
    }
}
