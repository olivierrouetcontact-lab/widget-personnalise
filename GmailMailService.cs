using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace MailWidget;

public sealed class GmailMailService : IDisposable
{
    private readonly string _tokenDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MailWidget",
        "GoogleToken");

    private GmailService? _service;

    public string CredentialsPath => Path.Combine(AppContext.BaseDirectory, "credentials.json");

    public async Task EnsureAuthorizedAsync(CancellationToken cancellationToken)
    {
        if (_service is not null)
        {
            return;
        }

        if (!File.Exists(CredentialsPath))
        {
            throw new FileNotFoundException(
                "Le fichier credentials.json est absent. Consulte le README pour configurer Gmail.",
                CredentialsPath);
        }

        await using var stream = new FileStream(CredentialsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
        var scopes = new[] { GmailService.Scope.GmailReadonly };

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            scopes,
            "mail-widget-user",
            cancellationToken,
            new FileDataStore(_tokenDirectory, true));

        _service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Gmail sur le bureau"
        });
    }

    public async Task<int> CountIncomingSinceAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        await EnsureAuthorizedAsync(cancellationToken);

        // Gmail's search language accepts a Unix timestamp for after:. The
        // exclusions keep sent, draft, spam, and trash messages out of the
        // incoming-mail count while retaining archived incoming messages.
        var request = _service!.Users.Messages.List("me");
        request.Q = $"after:{Math.Max(0, sinceUtc.ToUnixTimeSeconds())} -in:sent -in:drafts -in:spam -in:trash";
        request.MaxResults = 500;

        var total = 0;
        do
        {
            var page = await request.ExecuteAsync(cancellationToken);
            total += page.Messages?.Count ?? 0;
            request.PageToken = page.NextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(request.PageToken));

        return total;
    }

    public void Dispose()
    {
        _service?.Dispose();
    }
}
