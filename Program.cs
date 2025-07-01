using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Net;
using System.Text;
using System.Text.Json;

class Program
{
    private static TelegramBotClient Client = null!;
    private static ConcurrentDictionary<long, string> _userFileRequests = new ConcurrentDictionary<long, string>();
    private static CancellationTokenSource _cts = new CancellationTokenSource();

    static void StartHttpServer()
    {
        var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{port}/");
        listener.Start();

        Console.WriteLine($"HTTP server running on port {port}");

        Task.Run(async () =>
        {
            while (true)
            {
                var context = await listener.GetContextAsync();
                var req = context.Request;
                var resp = context.Response;

                if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/api/update")
                {
                    try
                    {
                        using var ms = new MemoryStream();
                        await req.InputStream.CopyToAsync(ms);
                        ms.Position = 0;

                        var update = JsonSerializer.Deserialize<Update>(ms, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                        if (update != null)
                        {
                            await HandleUpdateAsync(Client, update, _cts.Token);
                        }

                        resp.StatusCode = 200;
                        await resp.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("OK"));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error handling update: {ex}");
                        resp.StatusCode = 500;
                        await resp.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Error"));
                    }
                    resp.OutputStream.Close();
                }
                else if (req.HttpMethod == "GET" && (req.Url.AbsolutePath == "/" || req.Url.AbsolutePath == "/health"))
                {
                    var responseString = "🟢 Telegram bot is running.";
                    var buffer = Encoding.UTF8.GetBytes(responseString);
                    resp.ContentLength64 = buffer.Length;
                    await resp.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    resp.OutputStream.Close();
                }
                else
                {
                    resp.StatusCode = 404;
                    resp.OutputStream.Close();
                }
            }
        });
    }

    static async Task Main(string[] args)
    {
        StartHttpServer();

        var botToken = Environment.GetEnvironmentVariable("VOICES_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(botToken))
        {
            Console.WriteLine("❌ Bot token is missing! Set VOICES_BOT_TOKEN in Render environment.");
            return;
        }

        Client = new TelegramBotClient(botToken);

        var me = await Client.GetMe();
        Console.WriteLine($"@{me.Username} працює... вимкнути на ентер");

        await Client.DeleteWebhookAsync(dropPendingUpdates: true);

        await SetBotCommandsAsync();

        // IMPORTANT: Set webhook URL for your bot here, e.g.:
        var webhookUrl = $"https://your-render-service-url.com/api/update";
        await Client.SetWebhookAsync(webhookUrl);

        Console.WriteLine("Webhook set to: " + webhookUrl);

        // Keep the app running
        await Task.Delay(-1, _cts.Token);
    }

    private static async Task SetBotCommandsAsync()
    {
        await Client.DeleteMyCommands();

        var voicesFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Voices");
        var files = Directory.Exists(voicesFolder) ? Directory.GetFiles(voicesFolder) : Array.Empty<string>();

        var commands = new List<Telegram.Bot.Types.BotCommand>
        {
            new() { Command = "start", Description = "Старт" }
        };

        foreach (var file in files)
        {
            string fileName = Path.GetFileNameWithoutExtension(file);
            commands.Add(new() { Command = fileName, Description = fileName });
        }

        await Client.SetMyCommands(commands);
        Console.WriteLine("Команди виставлені");
    }

    private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Type == UpdateType.Message && update.Message is { } message)
        {
            await OnMessage(message);
        }
        else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } callbackQuery)
        {
            await OnUpdate(callbackQuery);
        }
    }

    private static async Task OnMessage(Telegram.Bot.Types.Message msg)
    {
        if (msg.Text == "/start")
        {
            await Client.SendMessage(msg.Chat.Id, "Для збереження гп відправ команду /save з назвою у відповідь на необхідне гп\n" +
                "Приклад: /saveexamplename\nДля відправки гп викликай команду з його назвою\nПриклад: /examplename\nВАЖЛИВО! Тільки латиниця, маленьки літери, ніяких особливих символів");
        }
        else if (msg.Type == MessageType.Text && msg.Text.Contains("/save") && msg.Text.Length > 5)
        {
            try
            {
                var fileId = msg.ReplyToMessage?.Voice?.FileId;
                if (fileId == null)
                {
                    await Client.SendMessage(msg.Chat.Id, "Будь ласка, відповідайте на голосове повідомлення командою /save");
                    return;
                }
                var fileName = msg.Text.Substring(5);
                var file = await Client.GetFileAsync(fileId);

                var voicesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Voices");
                if (!Directory.Exists(voicesDir)) Directory.CreateDirectory(voicesDir);
                var filePath = Path.Combine(voicesDir, $"{fileName}.ogg");

                using var saveStream = new FileStream(filePath, FileMode.Create);
                await Client.DownloadFile(file.FilePath, saveStream);
            }
            catch (Exception e)
            {
                Console.WriteLine("Помилка у відповіді або збереженні: " + e);
                await Client.SendMessage(msg.Chat.Id, "Виникла помилка :(");
            }
        }
        else if (msg.Type == MessageType.Text && msg.Text.StartsWith("/"))
        {
            await SendVoiceMessage(msg.Chat.Id, msg.Text.TrimStart('/'));
        }
    }

    private static async Task SendVoiceMessage(long chatId, string fileName)
    {
        var lastFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Voices", $"{fileName.Split('@')[0]}.ogg");
        try
        {
            using (var stream = new FileStream(lastFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Client.SendVoice(chatId, new InputFileStream(stream, Path.GetFileName(lastFile)));
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Нема файлу з назвою {fileName.Split('@')[0]}: {e.Message}");
            await Client.SendMessage(chatId, "Такого файлу нема :(");
        }
    }

    private static async Task OnUpdate(Telegram.Bot.Types.CallbackQuery query)
    {
        await Client.AnswerCallbackQueryAsync(query.Id, $"Ви обрали {query.Data}");
        await Client.SendMessage(query.Message.Chat.Id, $"Юзер {query.From.Username} клікнув на {query.Data}");
    }
}
