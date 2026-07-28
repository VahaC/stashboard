using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stashboard.Core.Abstractions;

/// <summary>
/// V9.2 — the contract shared between the parent Stashboard process (which
/// launches the self-update helper) and the <c>self-update</c> CLI mode the
/// helper runs in. The parent serializes the decrypted
/// <see cref="DockerUpdateProfile"/> into <see cref="ProfileEnvVar"/>; the
/// helper deserializes it with the same <see cref="Options"/> and runs the
/// recreate. Keeping the env-var names + serializer settings in one place stops
/// the two sides from drifting apart.
/// </summary>
public static class SelfUpdateProtocol
{
    /// <summary>Env var carrying the JSON-serialized <see cref="DockerUpdateProfile"/>.</summary>
    public const string ProfileEnvVar = "STASHBOARD_SELF_UPDATE_PROFILE";

    /// <summary>Env var carrying the seconds the helper waits before touching the
    /// parent container, so the parent's HTTP response can flush first.</summary>
    public const string DelayEnvVar = "STASHBOARD_SELF_UPDATE_DELAY_SECONDS";

    /// <summary>Default helper start delay when <see cref="DelayEnvVar"/> is unset.</summary>
    public const int DefaultStartDelaySeconds = 3;

    /// <summary>The CLI argument that puts the Stashboard image into one-shot
    /// self-update mode for a single container
    /// (<c>dotnet Stashboard.Api.dll self-update</c>).</summary>
    public const string CommandName = "self-update";

    /// <summary>The CLI argument for the whole-project variant
    /// (<c>dotnet Stashboard.Api.dll self-update-project</c>) — used when an
    /// "Update project" includes the Stashboard container itself.</summary>
    public const string ProjectCommandName = "self-update-project";

    /// <summary>Fixed name of the detached helper container. Shared so the
    /// launcher can clear a stale one and the helper can remove itself on
    /// success.</summary>
    public const string HelperContainerName = "stashboard-self-update";

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
