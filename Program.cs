using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Net;// bypassing render
using System.Text;// bypassing render

class Program
{
    private static TelegramBotClient Client = null!;
    private static ConcurrentDictionary<long, string> _userFileRequests = new ConcurrentDictionary<long, string>();
    private static CancellationTokenSource _cts = new CancellationTokenSource();
    private static void StartDummyHttpServer() // bypassing render
    {
        var port = Environment.GetEnvironmentVariable("PORT");
        if (string.IsNullOrEmpty(port))
        {
            port = "5000"; // fallback
        }
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{port}/");
        listener.Start();

        Console.WriteLine($"Dummy HTTP server running on port {port}");

        Task.Run(async () =>
        {
            while (true)
            {
                var context = await listener.GetContextAsync();
                var response = context.Response;
                string responseString = "🟢 Telegram bot is running.";
                byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.OutputStream.Close();
            }
        });
    }
    static async Task Main(string[] args)
    {
        StartDummyHttpServer(); // bypassing render

        var botToken = Environment.GetEnvironmentVariable("VOICES_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(botToken))
        {
            Console.WriteLine("❌ Bot token is missing! Set VOICES_BOT_TOKEN in Render environment.");
            return;
        }
        Client = new TelegramBotClient(botToken, cancellationToken: _cts.Token);
        var me = await Client.GetMe();

        Console.WriteLine($"@{me.Username} працює... вимкнути на ентер");

        await SetBotCommandsAsync();

        Client.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            },
            cancellationToken: _cts.Token
        );

        await Task.Delay(-1, _cts.Token);
    }

    private static async Task SetBotCommandsAsync()
    {
        await Client.DeleteMyCommands();

        var voicesFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Voices");
        var files = Directory.GetFiles(voicesFolder);

        var commands = new List<BotCommand>
        {
            new BotCommand { Command = "start", Description = "Старт" }
        };
        foreach (var file in files)
        {
            string fileName = Path.GetFileName(file).Split('.')[0];
            commands.Add(new BotCommand { Command = fileName, Description = fileName });
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

    private static async Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        Console.WriteLine(exception);
    }

    private static async Task OnMessage(Message msg)
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
                var fileId = msg.ReplyToMessage.Voice.FileId;
                var fileName = msg.Text.Substring(5);
                var file = await Client.GetFile(fileId);

                var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Voices", $"{fileName}.ogg");
                using (var saveStream = new FileStream(filePath, FileMode.Create))
                {
                    await Client.DownloadFile(file.FilePath, saveStream);
                }
            }
            catch(Exception e)
            {
                Console.WriteLine("Помилка у відповіді або збереженні");
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
            Console.WriteLine($"Нема файлу з назвою {fileName.Split('@')[0]}");
            Console.WriteLine(lastFile);
            await Client.SendMessage(chatId, "Такого файлу нема :(");
        }
    }

    private static async Task OnUpdate(CallbackQuery query)
    {
        await Client.AnswerCallbackQuery(query.Id, $"Ви обрали {query.Data}");
        await Client.SendMessage(query.Message.Chat.Id, $"Юзер {query.From.Username} клікнув на {query.Data}");
    }
}
