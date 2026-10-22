using System.Diagnostics;

namespace YaDiskFileSync;

/// <summary>
/// Вход в Яндекс: ClientID и секрет своего приложения с oauth.yandex.ru → короткий код →
/// пользователь вводит его на странице Яндекса. Пароль программа не видит.
/// </summary>
internal sealed class LoginForm : Form
{
    public const string RegisterUrl = "https://oauth.yandex.ru/client/new";

    private readonly HttpClient _http;
    private readonly TextBox _clientId = new() { Width = 340 };
    private readonly TextBox _secret = new() { Width = 340, UseSystemPasswordChar = true };
    private readonly Button _go = new() { Text = "Получить код", AutoSize = true };
    private readonly Label _code = new() { AutoSize = true, Font = new Font("Segoe UI", 22f, FontStyle.Bold), Visible = false };
    private readonly Label _hint = new() { AutoSize = true, MaximumSize = new Size(360, 0) };
    private readonly LinkLabel _open = new() { AutoSize = true, Visible = false };
    private CancellationTokenSource? _cts;

    public TokenSet? Token { get; private set; }
    public string ClientId => _clientId.Text.Trim();
    public string ClientSecret => _secret.Text.Trim();

    public LoginForm(HttpClient http, string? clientId, string? clientSecret)
    {
        _http = http;
        Text = "Вход в Яндекс";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = new Font("Segoe UI", 9.5f);
        Icon = IconArt.MakeIcon(32, IconArt.Blue);

        _clientId.Text = clientId ?? "";
        _secret.Text = clientSecret ?? "";

        var help = new LinkLabel { AutoSize = true, Text = "Где взять ClientID и секрет: зарегистрировать приложение на oauth.yandex.ru" };
        help.LinkClicked += (_, _) => Open(RegisterUrl);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { AutoSize = true, Text = "ClientID приложения:" });
        layout.Controls.Add(_clientId);
        layout.Controls.Add(new Label { AutoSize = true, Text = "Client secret (секретный ключ):" });
        layout.Controls.Add(_secret);
        layout.Controls.Add(help);
        layout.Controls.Add(_go);
        layout.Controls.Add(_code);
        layout.Controls.Add(_open);
        layout.Controls.Add(_hint);
        Controls.Add(layout);

        _go.Click += async (_, _) => await LoginAsync();
        _open.LinkClicked += (_, _) => Open(_open.Tag as string ?? "https://oauth.yandex.ru/device");
        AcceptButton = _go;
    }

    private async Task LoginAsync()
    {
        if (ClientId.Length == 0) { _hint.Text = "Укажите ClientID."; return; }
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
            _hint.Text = "Введите этот код на странице Яндекса и разрешите доступ. Окно закроется само.";
            try { Clipboard.SetText(dc.UserCode); _hint.Text += " Код уже скопирован."; } catch { }
            Open(dc.VerificationUrl);

            Token = await oauth.WaitForTokenAsync(dc, ct);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException) { }
        catch (OAuthException e)
        {
            _hint.Text = e.Message;
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

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
