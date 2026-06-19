namespace Stashboard.Core.Enums;

/// <summary>
/// V7.8 — where a container card's avatar comes from. <see cref="Auto"/> means
/// the backend resolves the official dashboard-icons logo from the image
/// reference; <see cref="Custom"/> means the user uploaded their own image.
/// </summary>
public enum ContainerIconSource
{
    Auto = 0,
    Custom = 1
}
