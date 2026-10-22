using System.Diagnostics;

namespace YaDiskFileSync;

/// <summary>
/// Вход в Яндекс с пошаговой инструкцией: зарегистрировать своё приложение на oauth.yandex.ru →
/// ввести его ClientID и секрет → подтвердить короткий код на странице Яндекса. Пароль программа не видит.
/// </summary>
internal sealed class LoginForm : Form
{
    public const string RegisterUrl = "https://oauth.yandex.ru/client/new";
    public const string RedirectUri = "https://oauth.yandex.ru/verification_code";
    public const string AppsUrl = "https://oauth.yandex.ru/";

    internal static readonly string[] Steps =
    {
        "Откройте oauth.yandex.ru (кнопка ниже) под своей учётной записью Яндекса и нажмите «Создать приложение».",
        "Название — любое, например «YaDiskSync». Платформа — «Веб-сервисы», Redirect URI — " + RedirectUri + " (кнопка «Скопировать»).",
        "В разделе доступов найдите «Яндекс.Диск REST API» и отметьте ТРИ права: «Чтение всего Диска», " +
            "«Запись в любом месте на Диске», «Доступ к информации о Диске». Без полного доступа программа не сможет " +
            "работать с выбранной папкой Диска. Почта для связи — ваша.",
        "Нажмите «Создать приложение» и скопируйте из его карточки ClientID и Client secret в поля ниже.",
        "Нажмите «Получить код» — откроется страница Яндекса: введите показанный здесь код и разрешите доступ. " +
            "Окно закроется само.",
    };

    private readonly HttpClient _http;
    private readonly TextBox _clientId = new() { Width = 420 };
    private readonly TextBox _secret = new() { Width = 420, UseSystemPasswordChar = true };
    private readonly Button _go = new() { Text = "Получить код", AutoSize = true };
    private readonly Label _code = new() { AutoSize = true, Font = new Font("Segoe UI", 22f, FontStyle.Bold), Visible = false };
    private readonly Label _hint = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly LinkLabel _open = new() { AutoSize = true, Visible = false };
    private CancellationTokenSource? _cts;

    public TokenSet? Token { get; private set; }
    /// <summary>Учётная запись, под которой вошли (проверена запросом к Диску).</summary>
    public DiskUser? User { get; private set; }
    private readonly Func<string, CancellationToken, Task<DiskUser>> _verify;
    public string ClientId => _clientId.Text.Trim();
    public string ClientSecret => _secret.Text.Trim();

    /// <param name="verify">Проверка нового токена: чья учётная запись и есть ли доступ ко всему Диску.</param>
    public LoginForm(HttpClient http, string? clientId, string? clientSecret, Func<string, CancellationToken, Task<DiskUser>> verify,
        bool rightsChanged = false)
    {
        _http = http;
        _verify = verify;
        Text = "Вход в Яндекс — YaDisk File Sync";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);
        Font = new Font("Segoe UI", 9.5f);
        Icon = IconArt.MakeIcon(32, IconArt.Blue);

        _clientId.Text = clientId ?? "";
        _secret.Text = clientSecret ?? "";

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(520, 0), Font = new Font(Font, FontStyle.Bold),
            Text = "Программа входит в Яндекс через ваше собственное приложение Яндекс ID. Его нужно один раз зарегистрировать:",
        });
        if (rightsChanged)
            layout.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = Color.FromArgb(180, 60, 0),
                Text = "В этой версии программа работает с любой папкой Диска, поэтому нужны новые права. Если приложение " +
                       "уже зарегистрировано — откройте его на oauth.yandex.ru, нажмите «Редактировать», отметьте права из шага 3 и войдите снова.",
            });
        for (int i = 0; i < Steps.Length; i++)
            layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(520, 0), Text = $"{i + 1}. {Steps[i]}", Margin = new Padding(3, 4, 3, 0) });

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 8) };
        var reg = new Button { Text = "Открыть oauth.yandex.ru", AutoSize = true };
        reg.Click += (_, _) => Open(RegisterUrl);
        var mine = new Button { Text = "Мои приложения", AutoSize = true };
        mine.Click += (_, _) => Open(AppsUrl);
        var copy = new Button { Text = "Скопировать Redirect URI", AutoSize = true };
        copy.Click += (_, _) => { try { Clipboard.SetText(RedirectUri); copy.Text = "Скопировано"; } catch { } };
        buttons.Controls.AddRange(new Control[] { reg, mine, copy });
        layout.Controls.Add(buttons);

        layout.Controls.Add(new Label { AutoSize = true, Text = "ClientID:" });
        layout.Controls.Add(_clientId);
        layout.Controls.Add(new Label { AutoSize = true, Text = "Client secret:" });
        layout.Controls.Add(_secret);
        layout.Controls.Add(_go);
        layout.Controls.Add(_code);
        layout.Controls.Add(_open);
        layout.Controls.Add(_hint);
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 10, 3, 0),
            Text = "ClientID, секрет и токены хранятся только на этом компьютере, зашифрованными для вашей учётной записи Windows. " +
                   "Отозвать доступ можно в любой момент на id.yandex.ru → Безопасность → Устройства и приложения.",
        });
        Controls.Add(layout);

        _go.Click += async (_, _) => await LoginAsync();
        _open.LinkClicked += (_, _) => Open(_open.Tag as string ?? "https://oauth.yandex.ru/device");
        AcceptButton = _go;
    }

    private async Task LoginAsync()
    {
        if (ClientId.Length == 0) { _hint.Text = "Укажите ClientID (шаг 4)."; return; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _go.Enabled = _clientId.Enabled = _secret.Enabled = false;
        _hint.Text = "Запрашиваю код…";
        try
        {
            var oauth = new YandexOAuth(_http, ClientId, ClientSecret.Length > 0 ? ClientSecret : null);
            var dc = await oauth.RequestDeviceCodeAsync(Environment.MachineName, ct);
            _code.Text = dc.UserCode;
            _code.Visible = true;
            _open.Text = "Открыть " + dc.VerificationUrl;
            _open.Tag = dc.VerificationUrl;
            _open.Visible = true;
            _hint.Text = "Введите этот код на странице Яндекса и нажмите «Разрешить». Окно закроется само.";
            try { Clipboard.SetText(dc.UserCode); _hint.Text += " Код уже скопирован."; } catch { }
            Open(dc.VerificationUrl);

            var token = await oauth.WaitForTokenAsync(dc, ct);
            _code.Visible = _open.Visible = false;
            _hint.Text = "Проверяю доступ к Диску…";
            User = await _verify(token.AccessToken, ct);
            Token = token;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException) { }
        catch (OAuthException e)
        {
            _hint.Text = e.Message;
            _code.Visible = _open.Visible = false;
        }
        catch (DiskException e)
        {
            // чаще всего 403: приложению не дали права на весь Диск
            _hint.Text = "Вход выполнен, но " + char.ToLowerInvariant(e.Message[0]) + e.Message[1..] + " Затем нажмите «Получить код» снова.";
            _code.Visible = _open.Visible = false;
        }
        finally
        {
            if (!IsDisposed) _go.Enabled = _clientId.Enabled = _secret.Enabled = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts?.Dispose();
        base.Dispose(disposing);
    }

    internal static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
