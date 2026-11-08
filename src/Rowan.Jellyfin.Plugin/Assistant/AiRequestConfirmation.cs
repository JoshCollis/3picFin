using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Rowan.Jellyfin.Plugin.Discovery;

namespace Rowan.Jellyfin.Plugin.Assistant;

/// <summary>This is an authenticated UI action, deliberately NOT a model tool.</summary>
public sealed class AiRequestConfirmation
{
    private readonly AiStore _store;
    private readonly Func<Guid, CreateSeerrRequest, CancellationToken, Task<SeerrCreateResult>> _submit;
    private readonly Func<Guid, CreateSeerrRequest, CreatedSeerrRequest, CancellationToken, Task<bool>> _verify;

    [ActivatorUtilitiesConstructor]
    public AiRequestConfirmation(AiStore store, SeerrClient seerr, IUserManager users, ILibraryManager libraries, IMediaSourceManager sources, Func<Guid, bool>? allowed = null)
        : this(store, (user, selection, token) => DiscoveryController.SubmitAsync(seerr, user, selection,
            (id, tmdb) => TitleDetailsController.ResolveLibrary(users, libraries, id, "movie", tmdb, null),
            (id, tmdb) => TitleDetailsController.ResolveLibrary(users, libraries, id, "movie", tmdb, null, item => TitleDetailsController.Is4kMovie(sources, item)), token,
            () => allowed?.Invoke(user) ?? (AiOptions.From(Plugin.Current?.Configuration) is not null && AiLibrary.Allowed(users.GetUserById(user)))),
            async (user, selection, created, token) =>
            {
                var read = DiscoveryDtos.Requests(await seerr.GetPersonalRequestsAsync(user, 100, 0, token).ConfigureAwait(false));
                return read.Error is null && read.Items.Any(r => r.Id == created.Id && r.TmdbId == selection.MediaId &&
                    (r.MediaType ?? r.Type) == selection.MediaType && r.Is4k == selection.Is4k);
            }) { }

    public AiRequestConfirmation(AiStore store,
        Func<Guid, CreateSeerrRequest, CancellationToken, Task<SeerrCreateResult>> submit,
        Func<Guid, CreateSeerrRequest, CreatedSeerrRequest, CancellationToken, Task<bool>> verify)
    { _store = store; _submit = submit; _verify = verify; }

    // Must run inside AiStore.Use: a second click, tab or replay cannot repeat the POST.
    public async Task Confirm(Guid user, AiUserData data, AiThread thread, Guid proposalId, bool confirm, Func<bool> authorized, CancellationToken token)
    {
        var proposal = thread.Messages.SelectMany(m => m.Proposals).SingleOrDefault(p => p.Id == proposalId) ?? throw new AiException("Confirmation not found.", 404);
        if (!authorized()) throw new AiException("The assistant is unavailable for this account.", 403);
        if (proposal.State != "pending") return;
        if (!confirm) { proposal.State = "cancelled"; proposal.Result = "Cancelled. No request submitted."; return; }
        if (proposal.ExpiresAt <= DateTimeOffset.UtcNow) { proposal.State = "expired"; proposal.Result = "Confirmation expired. Ask for a fresh request card."; return; }
        proposal.State = "submitting";
        await _store.Save(user, data, CancellationToken.None).ConfigureAwait(false);
        // Never automatically retry a potentially accepted POST, including after process restart.
        proposal.Result = "Request outcome unknown. Check My Requests before trying again.";
        proposal.State = "unknown";
        try
        {
            if (!authorized()) throw new AiException("The assistant is unavailable for this account.", 403);
            var outcome = await _submit(user, proposal.Selection, token).ConfigureAwait(false);
            if (outcome.Value is { } created)
            {
                if (await _verify(user, proposal.Selection, created, token).ConfigureAwait(false))
                { proposal.State = "submitted"; proposal.Result = "Request recorded in Seerr. Approval and downloads follow the server's normal settings."; }
            }
            else if (outcome.Failure is SeerrFailure.PermissionDenied or SeerrFailure.UserNotMapped or SeerrFailure.AlreadyRequested or SeerrFailure.InvalidSeason or SeerrFailure.CsrfUnsupported or SeerrFailure.Disabled)
            {
                proposal.State = "denied";
                proposal.Result = outcome.Failure switch
                {
                    SeerrFailure.PermissionDenied => "Seerr denied this request. Your quota, permissions or a blocklist may prevent it.",
                    SeerrFailure.AlreadyRequested => "Already available, requested, or no longer eligible. Reopen title details for fresh options.",
                    SeerrFailure.InvalidSeason => "The selected season is no longer valid. Ask for a fresh request card.",
                    SeerrFailure.CsrfUnsupported => "Seerr's CSRF configuration does not support this API-key request integration.",
                    _ => "Seerr is disabled or this account is not mapped. Ask your administrator to check the integration."
                };
            }
        }
        catch (OperationCanceledException) { /* An interrupted write is ambiguous, never proof it failed. */ }
        finally
        {
            thread.UpdatedAt = DateTimeOffset.UtcNow;
            await _store.Save(user, data, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
