namespace CodexQuotaWidget.Core.ControlCenter;

public sealed record SiwcModelChoice(string Slug, string DisplayName)
{
    public override string ToString() => DisplayName;
}
public sealed record SiwcAccountModels(string AccountId, IReadOnlyList<SiwcModelChoice> Models);

/// <summary>Account-scoped, public model directory. Model visibility alone is never quota or execution permission.</summary>
public sealed class SiwcModelCatalog(SiwcAuthorizationService auth, ISiwcProtocolTransport transport)
{
    public async Task<SiwcAccountModels> ReadAsync(string accountId, CancellationToken token = default)
    {
        var credential = await auth.GetAccessAsync(accountId, token).ConfigureAwait(false);
        if (credential.AccountId != accountId) throw new InvalidDataException("SIWC catalog account mismatch.");
        var response = await transport.ModelsAsync(credential.AccessToken, token).ConfigureAwait(false);
        SiwcIdToken.Unique(response);
        var source = response.GetProperty("models");
        if (source.GetArrayLength() > 256) throw new InvalidDataException("SIWC model catalog too large.");
        var models = new List<SiwcModelChoice>();
        foreach (var item in source.EnumerateArray())
        {
            SiwcIdToken.Unique(item);
            if (item.GetProperty("visibility").GetString() != "list") continue;
            var slug = SiwcAuthorizationAttempt.Required(item, "slug"); var display = SiwcAuthorizationAttempt.Required(item, "display_name");
            if (slug.Length > 128 || !slug.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                || display.Length > 256 || display.Any(char.IsControl) || models.Any(m => m.Slug == slug))
                throw new InvalidDataException("SIWC model catalog entry invalid.");
            models.Add(new(slug, display));
        }
        return new(accountId, models.AsReadOnly());
    }
}
