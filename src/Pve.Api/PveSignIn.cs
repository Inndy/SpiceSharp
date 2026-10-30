namespace Pve.Api;

/// <summary>A secret the user typed, and whether to keep it in Credential Manager.</summary>
public sealed record SecretAnswer(string Secret, bool Remember)
{
    public override string ToString() => $"SecretAnswer {{ Remember = {Remember} }}";
}

/// <summary>What a front end asks the user during sign-in. Returning null cancels.</summary>
public interface ISignInPrompt
{
    /// <param name="problem">Why it is asked again (e.g. the saved password was refused), or null the first time.</param>
    Task<SecretAnswer?> AskSecretAsync(PveServerConfig server, string? problem, CancellationToken ct);

    /// <summary>Returns the <c>password</c> value for the second step: <see cref="TfaChallenge.TotpResponse"/> or
    /// <see cref="TfaChallenge.RecoveryResponse"/>.</summary>
    Task<string?> AskSecondFactorAsync(PveServerConfig server, TfaChallenge challenge, string? problem, CancellationToken ct);
}

/// <summary>
/// The sign-in flow shared by front ends (docs/decisions/0039): use the saved secret if there is one, else ask;
/// check a token with <c>GET /version</c>; sign in with a password and answer the second factor; save the secret
/// when the user asked to. Refused secrets are asked for again; network and TLS errors go to the caller and leave the
/// client signed out.
/// </summary>
public static class PveSignIn
{
    const int MaxSecretAttempts = 5, MaxFactorAttempts = 3;

    /// <returns>True when signed in, false when the user cancelled.</returns>
    /// <remarks>The attempt replaces whatever session the client had: it starts signed out, so a cancelled prompt, a
    /// failing prompt or credential lookup, or a cancellation leaves no refused token or stale version behind
    /// (review R4). An accepted sign-in stays accepted if remembering its secret fails.</remarks>
    public static async Task<bool> SignInAsync(PveClient client, PveServerConfig server, CredentialStore? store, ISignInPrompt prompt,
        CancellationToken ct = default)
    {
        client.SignOut();
        string? target = store?.TargetFor(server);
        string? saved = target is null ? null : store!.Read(target);
        string? problem = null;
        for (int attempt = 0; attempt < MaxSecretAttempts; attempt++)
        {
            bool fromStore = attempt == 0 && saved is not null;
            SecretAnswer? answer = fromStore ? new SecretAnswer(saved!, false) : await prompt.AskSecretAsync(server, problem, ct).ConfigureAwait(false);
            if (answer is null) return false;
            try
            {
                if (server.Auth == PveAuthKind.Token)
                {
                    client.UseApiToken(server.TokenId, answer.Secret);
                    await client.GetVersionAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    var (step, challenge) = await client.LoginAsync(server.User, server.Realm, answer.Secret, ct).ConfigureAwait(false);
                    if (step == SignInStep.NeedsSecondFactor && !await SecondFactorAsync(client, server, challenge!, prompt, ct).ConfigureAwait(false))
                    {
                        client.SignOut();
                        return false;
                    }
                }
            }
            catch (PveAuthException e)
            {
                client.SignOut();
                problem = fromStore ? "The saved credential was refused; enter it again." : e.Message;
                continue;
            }
            catch
            {
                // A sign-in that didn't finish (server unreachable, TLS refused, ...) leaves no unchecked token or half
                // ticket behind, so the client doesn't count as signed in (smoke test B5).
                client.SignOut();
                throw;
            }
            if (answer.Remember && target is not null) store!.Write(target, server.Account, answer.Secret);
            return true;
        }
        return false;
    }

    static async Task<bool> SecondFactorAsync(PveClient client, PveServerConfig server, TfaChallenge challenge, ISignInPrompt prompt, CancellationToken ct)
    {
        if (!challenge.CanAnswer)
        {
            string others = challenge.UnsupportedFactors;
            throw new PveApiException(others.Length > 0
                ? $"This account's second factor ({others}) is not supported here; add a TOTP factor or use a recovery key."
                : "This account needs a second factor, but none it can use is set up.");
        }
        string? problem = null;
        for (int attempt = 0; attempt < MaxFactorAttempts; attempt++)
        {
            string? response = await prompt.AskSecondFactorAsync(server, challenge, problem, ct).ConfigureAwait(false);
            if (response is null) return false;
            try
            {
                await client.CompleteSecondFactorAsync(response, ct).ConfigureAwait(false);
                return true;
            }
            catch (PveAuthException) { problem = "The code was not accepted."; }
        }
        throw new PveAuthException("The second factor was refused too often.");
    }
}
