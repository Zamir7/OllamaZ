using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace OllamaWPF;

public partial class MainWindow : Window {
    private const string ConfigFile = "settings.ini";
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromMinutes(330) };
    private string _selectedFolder = "";
    private string _savedModel = "";
    private string _projectContext = "";
    private List<(string Role, string Content)> _history = new();

    public MainWindow() {
        InitializeComponent();
        LoadSettings();
        this.Closing += (s, e) => SaveSettings();
        _ = LoadModelsAsync();
    }

    private void LoadSettings() {
        if (!File.Exists(ConfigFile)) return;
        foreach (var line in File.ReadAllLines(ConfigFile)) {
            var p = line.Split('=', 2);
            if (p.Length != 2) continue;
            var k = p[0].Trim().ToLower(); var v = p[1].Trim();
            if (k == "model") _savedModel = v;
            if (k == "folder") { _selectedFolder = v; PathLabel.Text = v; }
            if (k == "stream") UseStreamCheck.IsChecked = v == "true";
        }
    }

    private void SaveSettings() {
        var m = ModelCombo.SelectedItem?.ToString() ?? _savedModel;
        var stream = UseStreamCheck?.IsChecked == true ? "true" : "false";
        File.WriteAllLines(ConfigFile, new[] { $"model={m}", $"folder={_selectedFolder}", $"stream={stream}" });
    }

    private async Task LoadModelsAsync() {
        try {
            var r = await _http.GetAsync("/api/tags");
            var j = await r.Content.ReadAsStringAsync();
            using var d = JsonDocument.Parse(j);
            var models = d.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => m.GetProperty("name").GetString())
                .Where(s => !string.IsNullOrEmpty(s)).ToList();
            Dispatcher.Invoke(() => {
                ModelCombo.ItemsSource = models;
                if (!string.IsNullOrEmpty(_savedModel) && models.Contains(_savedModel))
                    ModelCombo.SelectedItem = _savedModel;
                else if (models.Any()) ModelCombo.SelectedIndex = 0;
            });
        } catch { ModelCombo.ItemsSource = new[] { "⚠️ Ollama не запущен" }; }
    }

    private void PickFolder_Click(object sender, RoutedEventArgs e) {
        var dlg = new OpenFolderDialog { Title = "Выберите папку проекта" };
        if (dlg.ShowDialog() == true) {
            _selectedFolder = dlg.FolderName;
            PathLabel.Text = _selectedFolder;
            _projectContext = "";
            SaveSettings();
        }
    }

    private void ModelCombo_SelectionChanged(object s, System.Windows.Controls.SelectionChangedEventArgs e) {
        if (ModelCombo.SelectedItem != null) SaveSettings();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e) {
        _history.Clear();
        OutputBox.Clear();
        UpdateHistoryInfo();
    }

    private void SaveHistory_Click(object sender, RoutedEventArgs e) {
        if (!_history.Any()) return;
        var path = $"history_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        var lines = _history.Select(m => m.Role == "you" ? $"[YOU]: {m.Content}" : $"[MODEL]: {m.Content}");
        File.WriteAllLines(path, lines);
        MessageBox.Show($"История сохранена в {path}", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Ask_Click(object sender, RoutedEventArgs e) {
        if (string.IsNullOrEmpty(_selectedFolder) || ModelCombo.SelectedItem is not string model || string.IsNullOrWhiteSpace(QuestionBox.Text)) return;

        AskBtn.IsEnabled = false;
        OutputBox.Text = "📂 1/3 Читаю файлы...";
        var isStream = UseStreamCheck?.IsChecked == true;

        try {
            // ЭТАП 1: Чтение контекста
            if (UseContextCheck?.IsChecked == true && string.IsNullOrEmpty(_projectContext)) {
                _projectContext = await Task.Run(() => ReadFolder(_selectedFolder));
                OutputBox.Text = isStream ? "📦 2/3 Контекст собран (потоковый режим)..." : "📦 2/3 Контекст собран, собираю запрос...";
            } else if (UseContextCheck?.IsChecked == true) {
                OutputBox.Text = isStream ? "📦 2/3 Контекст в памяти (потоковый режим)..." : "📦 2/3 Контекст в памяти, собираю запрос...";
            } else {
                OutputBox.Text = isStream ? "💬 2/3 Без контекста (потоковый режим)..." : "💬 2/3 Без контекста, собираю запрос...";
            }

            // ЭТАП 2: Сборка сообщений
            var messages = new List<object>();
            if (UseContextCheck?.IsChecked == true && !string.IsNullOrEmpty(_projectContext)) {
                messages.Add(new { role = "system", content = $"Ты — ассистент .NET разработчика. Анализируй контекст ниже. Отвечай на русском.\n\nКонтекст проекта:\n{_projectContext}" });
            }

            if (UseHistoryCheck?.IsChecked == true) {
                foreach (var msg in _history)
                    messages.Add(new { role = msg.Role == "you" ? "user" : "assistant", content = msg.Content });
            }
            messages.Add(new { role = "user", content = QuestionBox.Text });

            OutputBox.Text += isStream ? "\n📡 3/3 Отправляю в Ollama (потоковый ответ)..." : "\n📡 3/3 Отправляю в Ollama и жду ответ...";

            // ЭТАП 3: Запрос
            var payload = new { model = model, messages = messages, stream = isStream, options = new { num_ctx = 8192, temperature = 0.2 } };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = content };
            var resp = await _http.SendAsync(request, isStream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead);

            string answer;
            if (isStream) {
                var sb = new StringBuilder();
                OutputBox.Text += "\n🔄 Читаю поток...";

                using var stream = await resp.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream) {
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try {
                        using var chunk = JsonDocument.Parse(line);
                        if (chunk.RootElement.TryGetProperty("done", out var done) && done.GetBoolean()) break;

                        if (chunk.RootElement.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("content", out var contentEl)) {
                            var part = contentEl.GetString();
                            if (!string.IsNullOrEmpty(part)) {
                                sb.Append(part);
                                // 🔹 Применяем очистку от Markdown
                                OutputBox.Text = StripMarkdown(sb.ToString());
                            }
                        }
                    } catch { /* игнорируем битые чанки */ }
                }
                answer = sb.ToString();
            } else {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                answer = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "Пустой ответ";
                // 🔹 Применяем очистку от Markdown
                OutputBox.Text = StripMarkdown(answer);
            }

            if (UseHistoryCheck?.IsChecked == true) {
                _history.Add(("you", QuestionBox.Text));
                _history.Add(("assistant", answer));
                UpdateHistoryInfo();
            }
        } catch (Exception ex) {
            OutputBox.Text = $"❌ Ошибка:\n{ex.GetType().Name}: {ex.Message}\n\n💡 Если TaskCanceledException → увеличь Timeout в HttpClient.";
        } finally { AskBtn.IsEnabled = true; }
    }

    private void UpdateHistoryInfo() => HistoryInfo.Text = $"Сообщений: {_history.Count / 2}";

    private string ReadFolder(string path) {
        var sb = new StringBuilder();
        var meta = Directory.EnumerateFiles(path, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories));
        foreach (var f in meta) {
            var content = File.ReadAllText(f);
            if (content.Contains("Password", StringComparison.OrdinalIgnoreCase)) continue;
            sb.AppendLine($"// 📄 КОНФИГ: {Path.GetRelativePath(path, f)}\n{content}\n");
        }

        var files = Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\.git\\"));
        foreach (var f in files) {
            var rel = Path.GetRelativePath(path, f);
            var txt = File.ReadAllText(f);
            if (txt.Contains("Password", StringComparison.OrdinalIgnoreCase)) continue;
            if (txt.Length > 1500) txt = txt[..1500] + "\n// ...обрезано";
            sb.AppendLine($"// 📄 {rel}\n{txt}\n");
        }
        return sb.ToString();
    }

    private async void CopyContext_Click(object sender, RoutedEventArgs e) {
        if (string.IsNullOrEmpty(_selectedFolder)) {
            MessageBox.Show("Сначала выберите папку проекта.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CopyContextBtn.Content = "⏳ Читаю файлы...";
        CopyContextBtn.IsEnabled = false;

        try {
            if (string.IsNullOrEmpty(_projectContext))
                _projectContext = await Task.Run(() => ReadFolder(_selectedFolder));

            System.Windows.Clipboard.SetText(_projectContext);
            CopyContextBtn.Content = "✅ Скопировано!";
            await Task.Delay(2000);
            CopyContextBtn.Content = "📋 Копировать контекст";
        } catch (Exception ex) {
            MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            CopyContextBtn.Content = "📋 Копировать контекст";
        } finally { CopyContextBtn.IsEnabled = true; }
    }

    // 🔹 НОВЫЙ МЕТОД: убирает **жирный**, *курсив*, `код` из текста
    private string StripMarkdown(string text) {
        if (string.IsNullOrEmpty(text)) return text;
        // **bold** → bold
        text = Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");
        // *italic* → italic
        text = Regex.Replace(text, @"\*([^*]+)\*", "$1");
        // `code` → code
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        // ### Заголовок → Заголовок
        text = Regex.Replace(text, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        return text;
    }
}