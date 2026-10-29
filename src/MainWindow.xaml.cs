using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DataSweeper
{
    public class Finding
    {
        public string Source { get; set; } = "local";
        public int Row { get; set; }
        public string Column { get; set; } = "";
        public string Issue { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public partial class MainWindow : Window
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

        private string? _file;
        private string _baseUrl = "https://aicongressonline.com";
        private List<Finding> _findings = new();
        private int _totalLines;
        private int _dataRows;

        public MainWindow()
        {
            InitializeComponent();
            var args = Environment.GetCommandLineArgs();
            var files = args.Skip(1).Where(a => File.Exists(a)).ToList();
            foreach (var a in args.Skip(1))
                if (a.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { _baseUrl = a.TrimEnd('/'); CloudUrl.Text = _baseUrl; }
            if (files.Count > 0)
            {
                Select(files[0]);
                Scan(autoExport: true);
            }
            if (args.Contains("--auto-cloud"))
                _ = CloudFlowAsync();
        }

        private static string? FirstFile(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            var items = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            return items?.FirstOrDefault(File.Exists);
        }

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DropZone.BorderBrush = e.Effects == DragDropEffects.Copy
                ? new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED))
                : new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2));
            e.Handled = true;
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            var f = FirstFile(e);
            if (f == null) { Status.Text = "No usable file in the drop"; return; }
            Select(f);
        }

        private void DropZone_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Choose a file to scan", Filter = "CSV files|*.csv|All files|*.*" };
            if (dlg.ShowDialog() == true) Select(dlg.FileName);
        }

        private void Select(string path)
        {
            _file = path;
            var info = new FileInfo(path);
            Hint.Text = "Selected: " + info.Name;
            FileLine.Text = info.FullName + "  (" + info.Length.ToString("N0") + " bytes)";
            Status.Text = "Ready to scan";
            StartButton.IsEnabled = true;
            CloudButton.IsEnabled = true;
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;
            await Task.Run(() => Scan(autoExport: false));
            StartButton.IsEnabled = true;
        }

        private void CloudButton_Click(object sender, RoutedEventArgs e) => _ = CloudFlowAsync();

        private static List<string> SplitCsv(string line)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else quoted = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { cells.Add(sb.ToString().Trim()); sb.Clear(); }
                else sb.Append(c);
            }
            cells.Add(sb.ToString().Trim());
            return cells;
        }

        private static bool LooksLike(string column, params string[] keys) =>
            keys.Any(k => column.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

        private void Scan(bool autoExport)
        {
            if (_file == null) { Status.Text = "Choose a file first"; return; }
            var sw = Stopwatch.StartNew();
            var findings = new List<Finding>();
            List<string> header = new();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            int lineNo = 0;

            using (var reader = new StreamReader(_file, Encoding.UTF8, true))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNo++;
                    if (lineNo == 1) { header = SplitCsv(line); continue; }
                    if (line.Length == 0) continue;
                    var cells = SplitCsv(line);
                    if (cells.Count != header.Count)
                    {
                        findings.Add(new Finding
                        {
                            Row = lineNo,
                            Column = "(row)",
                            Issue = "Ragged row (expected " + header.Count + " columns, found " + cells.Count + ")",
                            Value = ""
                        });
                        continue;
                    }
                    for (int c = 0; c < cells.Count; c++)
                        CheckCell(findings, lineNo, header[c], cells[c]);

                    var key = string.Join("\u0001", cells);
                    if (seen.TryGetValue(key, out int first))
                        findings.Add(new Finding { Row = lineNo, Column = "(row)", Issue = "Duplicate row (identical to row " + first + ")", Value = "" });
                    else seen[key] = lineNo;
                }
            }

            sw.Stop();
            _findings = findings;
            _totalLines = lineNo;
            _dataRows = Math.Max(0, lineNo - 1);

            Dispatcher.Invoke(() =>
            {
                Results.ItemsSource = findings;
                ExportButton.IsEnabled = findings.Count > 0;
                Summary.Text = "local: " + _totalLines + " lines (" + _dataRows + " data rows), " + findings.Count + " dirty entries";
                Status.Text = "Local scan: " + findings.Count + " issues in " + sw.ElapsedMilliseconds + " ms";
                if (autoExport)
                {
                    var outPath = Path.ChangeExtension(_file, ".report.csv");
                    ExportTo(outPath);
                    Status.Text += "; report exported: " + outPath;
                }
            });
        }

        private static void CheckCell(List<Finding> findings, int rowNo, string column, string value)
        {
            if (value.Length == 0)
            {
                findings.Add(new Finding { Row = rowNo, Column = column, Issue = "Missing value", Value = "" });
                return;
            }
            if (LooksLike(column, "日期", "date"))
            {
                if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    findings.Add(new Finding { Row = rowNo, Column = column, Issue = "Invalid date (expects YYYY-MM-DD)", Value = value });
            }
            else if (LooksLike(column, "邮箱", "email", "mail"))
            {
                if (!Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$"))
                    findings.Add(new Finding { Row = rowNo, Column = column, Issue = "Invalid email", Value = value });
            }
            else if (LooksLike(column, "年龄", "金额", "数量", "age", "amount", "qty", "price"))
            {
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                    findings.Add(new Finding { Row = rowNo, Column = column, Issue = "Invalid number", Value = value });
            }
        }

        // ---- cloud bridge: device code -> scoped token -> upload -> result ----

        private async Task CloudFlowAsync()
        {
            if (_file == null) { Status.Text = "Choose a file first"; return; }
            _baseUrl = string.IsNullOrWhiteSpace(CloudUrl.Text) ? _baseUrl : CloudUrl.Text.TrimEnd('/');
            CloudPanel.Visibility = Visibility.Visible;
            CloudButton.IsEnabled = false;
            try
            {
                var bytes = await File.ReadAllBytesAsync(_file);
                string sha;
                using (var h = SHA256.Create()) sha = Convert.ToHexString(h.ComputeHash(bytes)).ToLowerInvariant();

                CloudStatus.Text = "Cloud: opening a hand-off code…";
                var startReq = new { device_fp = Environment.MachineName, file_sha256 = sha, bytes = bytes.Length, engine = "local-csharp" };
                using var startResp = await Http.PostAsync(_baseUrl + "/api/v1/link/start",
                    new StringContent(JsonSerializer.Serialize(startReq), Encoding.UTF8, "application/json"));
                var startBody = await startResp.Content.ReadAsStringAsync();
                if (!startResp.IsSuccessStatusCode) throw new Exception("start " + (int)startResp.StatusCode + ": " + startBody);
                using var startDoc = JsonDocument.Parse(startBody);
                var code = startDoc.RootElement.GetProperty("code").GetString() ?? "";
                var verify = startDoc.RootElement.GetProperty("verify_url").GetString() ?? "";
                var pay = startDoc.RootElement.TryGetProperty("pay_url", out var pu) && pu.ValueKind == JsonValueKind.String ? (pu.GetString() ?? "") : "";
                var deviceToken = startDoc.RootElement.TryGetProperty("device_token", out var dt) && dt.ValueKind == JsonValueKind.String ? (dt.GetString() ?? "") : "";
                CloudCode.Text = code;
                var target = pay.Length > 0 ? pay : verify;
                var opened = false;
                if (target.Length > 0)
                {
                    try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); opened = true; }
                    catch { opened = false; }
                }
                CloudStatus.Text = opened
                    ? "Cloud: payment page opened in your browser — waiting for the payment to arrive…"
                    : "Cloud: could not open a browser. Open this link to pay — " + target;
                Status.Text = opened
                    ? "Cloud hand-off code " + code + " — pay in the browser, this window does the rest"
                    : "Cloud hand-off code " + code;

                string? token = null;
                string? paidToken = null;
                var deadline = DateTime.UtcNow.AddMinutes(10);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(2000);
                    var statusUrl = _baseUrl + "/api/v1/link/status?code=" + Uri.EscapeDataString(code) +
                                    (deviceToken.Length > 0 ? "&device_token=" + Uri.EscapeDataString(deviceToken) : "");
                    using var stResp = await Http.GetAsync(statusUrl);
                    var stBody = await stResp.Content.ReadAsStringAsync();
                    if (!stResp.IsSuccessStatusCode) continue;
                    using var stDoc = JsonDocument.Parse(stBody);
                    var st = stDoc.RootElement.GetProperty("status").GetString();
                    if (st == "expired") throw new Exception("the code expired before approval");
                    if (st != "approved" && st != "paid") continue;
                    if (stDoc.RootElement.TryGetProperty("token", out var tk) && tk.ValueKind == JsonValueKind.String)
                        token = tk.GetString();
                    if (stDoc.RootElement.TryGetProperty("paid_token", out var pt) && pt.ValueKind == JsonValueKind.String)
                        paidToken = pt.GetString();
                    break;
                }
                if (string.IsNullOrEmpty(token)) throw new Exception("no approval inside 10 minutes");
                if (string.IsNullOrEmpty(paidToken))
                    throw new Exception("payment required — approve the device and pay on the page, then press Send to cloud again");

                CloudStatus.Text = "Cloud: approved — uploading (scoped token, 15 min)…";
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent("csharp"), "engine");
                var fileContent = new ByteArrayContent(bytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
                form.Add(fileContent, "file", Path.GetFileName(_file));
                using var upReq = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/api/v1/link/jobs") { Content = form };
                upReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                upReq.Headers.Add("X-Paid-Token", paidToken);
                using var upResp = await Http.SendAsync(upReq);
                var upBody = await upResp.Content.ReadAsStringAsync();
                if (!upResp.IsSuccessStatusCode) throw new Exception("upload " + (int)upResp.StatusCode + ": " + upBody);
                using var jobDoc = JsonDocument.Parse(upBody);
                var jobId = jobDoc.RootElement.GetProperty("job_id").GetString() ?? "";
                var rows = jobDoc.RootElement.GetProperty("rows").GetInt32();
                var found = jobDoc.RootElement.GetProperty("findings").GetInt32();

                CloudStatus.Text = "Cloud: job " + jobId.Substring(0, 8) + " done — fetching the report…";
                using var resReq = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/api/v1/link/jobs/" + jobId + "/result");
                resReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resResp = await Http.SendAsync(resReq);
                var csv = await resResp.Content.ReadAsStringAsync();
                if (!resResp.IsSuccessStatusCode) throw new Exception("result " + (int)resResp.StatusCode);

                var cloudFindings = new List<Finding>();
                var lines = csv.Replace("\r\n", "\n").Split('\n');
                foreach (var line in lines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var cells = SplitCsv(line);
                    if (cells.Count < 4) continue;
                    if (!int.TryParse(cells[0], out var rn)) continue;
                    cloudFindings.Add(new Finding { Source = "cloud", Row = rn, Column = cells[1], Issue = cells[2], Value = cells[3] });
                }
                _findings = cloudFindings;
                Results.ItemsSource = cloudFindings;
                ExportButton.IsEnabled = cloudFindings.Count > 0;
                Summary.Text = "cloud: " + rows + " data rows, " + found + " dirty entries (local report kept separately)";
                CloudStatus.Text = "Cloud: job " + jobId.Substring(0, 8) + " imported — " + rows + " rows, " + found + " findings";
                Status.Text = "Cloud result imported: " + found + " issues (engine csharp, cloud copy)";
            }
            catch (Exception ex)
            {
                CloudStatus.Text = "Cloud: " + FriendlyCloudError(ex.Message);
                Status.Text = "Cloud hand-off failed — your local scan and report are unaffected";
            }
            finally
            {
                CloudButton.IsEnabled = true;
            }
        }

        private static string FriendlyCloudError(string raw)
        {
            var m = (raw ?? "").ToLowerInvariant();
            if (m.Contains("invalid license") || m.Contains("license not recognised"))
                return "that licence code doesn't match any purchase. Copy the code from your Gumroad receipt and try again.";
            if (m.Contains("payment required"))
                return "the payment hasn't landed yet. Finish the checkout in your browser, then press Send to cloud again.";
            if (m.Contains("expired"))
                return "this hand-off code has expired. Press Send to cloud to start a fresh one.";
            if (m.Contains("no approval inside"))
                return "we didn't see the payment within 10 minutes. Press Send to cloud to try again.";
            if (m.Contains("413") || m.Contains("too large"))
                return "this file is larger than the plan allows (5 MB on the free tier).";
            if (m.Contains("401") || m.Contains("403"))
                return "the server didn't accept this request. Make sure you're using the email you paid with, then try again.";
            return "we couldn't reach the server just now. Please try again in a minute.";
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_file == null || _findings.Count == 0) return;
            var dlg = new SaveFileDialog
            {
                Title = "Export error report",
                Filter = "CSV files|*.csv",
                FileName = Path.GetFileNameWithoutExtension(_file) + ".report.csv"
            };
            if (dlg.ShowDialog() == true)
            {
                ExportTo(dlg.FileName);
                Status.Text = "Report exported: " + dlg.FileName + " (" + _findings.Count + " rows)";
            }
        }

        private void ExportTo(string path)
        {
            static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            using var w = new StreamWriter(path, false, new UTF8Encoding(true));
            w.WriteLine("Source,Row,Column,Issue,Value");
            foreach (var f in _findings)
                w.WriteLine(f.Source + "," + f.Row + "," + Q(f.Column) + "," + Q(f.Issue) + "," + Q(f.Value));
        }
    }
}
