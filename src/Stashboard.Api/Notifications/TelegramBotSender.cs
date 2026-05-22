using System.Net.Http.Json;

namespace Stashboard.Api.Notifications;

public sealed class TelegramBotSender(HttpClient httpClient) : ITelegramSender
{
    public async Task SendMessageAsync(string botToken, string chatId, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        using var response = await httpClient.PostAsJsonAsync(
            $"https://api.telegram.org/bot{botToken}/sendMessage",
            new
            {
                chat_id = chatId,
                text = message,
                disable_web_page_preview = true,
            },
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }
}
