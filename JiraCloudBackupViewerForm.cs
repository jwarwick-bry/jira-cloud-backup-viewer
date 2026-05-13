using Markdig;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace JiraCloudBackupViewer
{
    public partial class JiraCloudBackupViewerForm : Form
    {
        private XDocument XDocument { get; set; }

        private Dictionary<string, SearchIssue> Issues { get; set; }
            = new Dictionary<string, SearchIssue>();
        private Dictionary<string, string> Users{ get; set; }
            = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

        public List<SearchIssue> SearchResults { get; set; }

        public JiraCloudBackupViewerForm()
        {
            InitializeComponent();
        }

        private async void Form1_Load(object sender, EventArgs e)
        {
            await InitializeAsync();

            cueBanner1.SetCueBannerText(textBox1, "Search by keyword(s)");
            OpenEntitiesXml();            
        }
        private async Task InitializeAsync()
        {
            await webView21.EnsureCoreWebView2Async(null);
            webView21.WebMessageReceived += WebView21_WebMessageReceived;
        }

        private void WebView21_WebMessageReceived(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var msg = JsonDocument.Parse(e.WebMessageAsJson);
                var action = msg.RootElement.GetProperty("action").GetString();
                var path = msg.RootElement.GetProperty("path").GetString();
                // Path.GetFileName strips any directory components from the filename to prevent path traversal
                var filename = Path.GetFileName(msg.RootElement.GetProperty("filename").GetString());

                if (!File.Exists(path)) return;

                if (action == "preview")
                {
                    // Copy to a temp file with the original filename so the OS picks the correct default app
                    var tempDir = Path.Combine(Path.GetTempPath(), "JiraCloudBackupViewer");
                    Directory.CreateDirectory(tempDir);
                    var tempPath = Path.Combine(tempDir, filename);
                    File.Copy(path, tempPath, overwrite: true);
                    // UseShellExecute opens the file with the user's default application
                    Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
                }
                else if (action == "download")
                {
                    using var sfd = new SaveFileDialog();
                    sfd.FileName = filename;
                    if (sfd.ShowDialog() == DialogResult.OK)
                    {
                        File.Copy(path, sfd.FileName, overwrite: true);
                    }
                }
            }
            catch (JsonException) { /* ignore malformed messages */ }
            catch (IOException) { /* ignore file access errors */ }
        }

        private void CleanupTempFiles()
        {
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "JiraCloudBackupViewer");
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException) { /* best-effort cleanup */ }
        }

        private string basePath;

        private void LoadFile(string filename)
        {
            toolStripStatusLabel1.Text = $"Loading {filename}, please wait..";
            Application.DoEvents();

            basePath = Path.GetDirectoryName(filename);

            //Fix invalid chars and load Xml
            var s = File.ReadAllText(filename, System.Text.Encoding.UTF8);
            // Remove invalid XML control characters (excluding tab \x09, LF \x0A, CR \x0D)
            s = Regex.Replace(s, "[\x00-\x08\x0B\x0C\x0E-\x1F]", "", RegexOptions.Compiled);
            // Replace bare & not part of a valid XML entity reference with &amp;
            s = Regex.Replace(s, @"&(?!([a-zA-Z][a-zA-Z0-9]*|#[0-9]+|#x[0-9a-fA-F]+);)", "&amp;", RegexOptions.Compiled);
            using (var sr = new StringReader(s))
                XDocument = XDocument.Load(sr);

            Issues = XDocument.Descendants("Issue").ToDictionary(i => i.Attribute("id").Value, i => new SearchIssue { Issue = i });
            Users = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var u in XDocument.Descendants("User"))
            {
                var displayName = u.Attribute("displayName")?.Value;
                AddUserMapping(u.Attribute("userName")?.Value, displayName);
                AddUserMapping(u.Attribute("userKey")?.Value, displayName);
                AddUserMapping(u.Attribute("name")?.Value, displayName);
            }

            foreach (var a in XDocument.Descendants("Action"))
            {
                var issueid = a.Attribute("issue").Value;
                if (Issues.ContainsKey(issueid))
                {
                    Issues[issueid].Actions.Add(new SearchAction { Action = a });
                }
            }
            foreach (var fa in XDocument.Descendants("FileAttachment"))
            {
                var issueid = fa.Attribute("issue").Value;
                if (Issues.ContainsKey(issueid))
                {
                    Issues[issueid].FileAttachments.Add(new SearchFileAttachment { FileAttachment = fa });
                }
            }

            LoadApprovalsFromActiveObjects();

            toolStripStatusLabel1.Text = $"{filename} loaded. Use search keywords to look for something.";
        }

        private void textBox1_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                Search(textBox1.Text);
            }                
        }

        private void Search(string text)
        {   
            var results = Issues.Values.AsEnumerable();
            var keywords = text.Split(" ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var k in keywords)
            {
                results = results.Where(r => r.IsMatch(k));
            }
            SearchResults = results.ToList();
            dataGridView1.AutoGenerateColumns = false;
            dataGridView1.DataSource = new SortableBindingList<SearchIssue>(SearchResults);
        }

        private void dataGridView1_SelectionChanged(object sender, EventArgs e)
        {
            if (dataGridView1.SelectedRows.Count > 0)
            {
                var si = dataGridView1.SelectedRows[0].DataBoundItem as SearchIssue;
                dataGridView2.DataSource = new SortableBindingList<SearchAction>((dataGridView1.SelectedRows[0].DataBoundItem as SearchIssue).Actions);
                textBoxIssue.Text = si.Description; // Jira2Md(si.Description ?? string.Empty);


                var sections = new List<string>();
                sections.Add(string.Concat(si.FileAttachments
                    .Select(fa =>
                    {
                        var localPath = Path.Combine(basePath, "data", "attachments", si.ProjectKey, "10000", si.IssueNr, fa.Id);
                        var htmlFilename = HtmlE(fa.Filename);
                        var jsLocalPath = JsStringE(localPath);
                        var jsFilename = JsStringE(fa.Filename);
                        return $@"<strong>{htmlFilename}</strong>
                        <a href='#' onclick=""hostAction('preview','{jsLocalPath}','{jsFilename}'); return false;"">preview</a>
                        <a href='#' onclick=""hostAction('download','{jsLocalPath}','{jsFilename}'); return false;"">download</a>
                        <br />";
                    })));
                sections.Add(Md2Html(Jira2Md(textBoxIssue.Text ?? string.Empty)));
                sections.AddRange(si.Actions.OrderBy(a => a.Created).Select(a =>
                {
                    var displayName = ResolveUserDisplayName(a.Author);
                    return $"<h3 class='jcv';>{HtmlE(a.Type)} - {a.Created} - {HtmlE(displayName)}</h3>\n    {Md2Html(Jira2Md(a.Body ?? string.Empty))}";
                }));
                if (si.Approvals.Any())
                {
                    sections.Add($@"<h3 class='jcv jcv-approvals-title'>Approvals</h3>
<table class='jcv-approvals'>
<thead>
<tr><th>Approver</th><th>Role</th><th>Approved at</th><th>Status</th></tr>
</thead>
<tbody>
{string.Concat(si.Approvals.OrderBy(a => a.ApprovedAt ?? DateTime.MaxValue).Select(a => $"<tr><td>{HtmlE(a.Approver)}</td><td>{HtmlE(a.Role)}</td><td>{HtmlE(a.ApprovedAt?.ToString() ?? string.Empty)}</td><td>{HtmlE(a.Status)}</td></tr>"))}
</tbody>
</table>");
                }
                webView21.NavigateToString(@$"<style>body {{ font-family: sans-serif; }} h3.jcv {{
    color:navy;
    background-color: rgba(0,0,100,0.05);
    padding: 5px;font-family: monospace;
    border: 1px solid navy;
    border-style: solid none;
}}
table.jcv-approvals {{
    border-collapse: collapse;
    width: 100%;
}}
table.jcv-approvals th, table.jcv-approvals td {{
    border: 1px solid #d0d7de;
    text-align: left;
    padding: 6px;
    font-size: 0.95rem;
}}
h3.jcv-approvals-title {{
    margin-top: 24px;
}}
</style>
<script>
function hostAction(action, path, filename) {{
    window.chrome.webview.postMessage(JSON.stringify({{action: action, path: path, filename: filename}}));
}}
</script>

                    <section><h2 style='color:navy;'>{HtmlE(si.IssueNr)} - {si.Created} - {HtmlE(si.Summary)}</h2>
                    {string.Join($"</section><section>", sections)}</section>"); 

                textBoxIssue.Text = textBoxIssue.Text.Replace("\r", "").Replace("\n", "\r\n");
            }
            else
                dataGridView2.DataSource = null;
        }

        private void JiraCloudBackupViewerForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            CleanupTempFiles();
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenEntitiesXml();
        }

        private void OpenEntitiesXml()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.FileName = "entities.xml";
                ofd.Filter = "XML files|*.xml";
                ofd.Title = "Select Jira Cloud export file (entities.xml)";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    LoadFile(ofd.FileName);
                    splitContainer1.Enabled = true;
                }
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            Search(textBox1.Text);
        }


        private string Jira2Md(string jira)
        {
            //   return str
            // Ordered Lists
            jira = Regex.Replace(jira, @"^[ \t]*(\*+)\s+",
                match => string.Join("  ", Enumerable.Repeat(string.Empty, match.Groups[1].Length)) + "* ",
                RegexOptions.Multiline);

            // Un-ordered lists
            jira = Regex.Replace(jira, @"^[ \t]*(#+)\s+",
                match => string.Join("  ", Enumerable.Repeat(string.Empty, match.Groups[1].Length)) + "1. ",
                RegexOptions.Multiline);

            // Headers 1-6
            jira = Regex.Replace(jira, @"^h([0-6])\.(.*)$",
                match => string.Join("#", Enumerable.Repeat(string.Empty, int.Parse(match.Groups[1].Value) + 1)) + " " + match.Groups[2].Value.TrimStart(),
                RegexOptions.Multiline);
            // Bold
            jira = Regex.Replace(jira, @"\*(\S.*)\*", "**$1**");
            // Italic
            jira = Regex.Replace(jira, @"_(\S.*)_", "*$1*");
            // Monospaced text
            jira = Regex.Replace(jira, @"\{\{([^}]+)\}\}", "`$1`");
            // Citations (buggy)
            //jira = Regex.Replace(jira, @"\?\?((?:.[^?]|[^?].)+)\?\?", "<cite>$1</cite>");
            // Inserts
            jira = Regex.Replace(jira, @"\+([^+]*)\+", "<ins>$1</ins>");
            // Superscript
            jira = Regex.Replace(jira, @"\^([^^]*)\^", "<sup>$1</sup>");
            // Subscript
            jira = Regex.Replace(jira, @"~([^~]*)~", "<sub>$1</sub>");
            // Strikethrough
            jira = Regex.Replace(jira, @"(\s+)-(\S+.*?\S)-(\s+)", "$1~~$2~~$3");
            // Code Block
            jira = new Regex(@"\{code(:([a-z]+))?([:|]?(title|borderStyle|borderColor|borderWidth|bgColor|titleBGColor)=.+?)*\}(.*?)\n?\{code\}",
                RegexOptions.Singleline, RegexTimeout).Replace(jira, "```$2$5\n```");
            // Pre-formatted text
            jira = Regex.Replace(jira, @"{noformat}", "```");
            // Un-named Links
            jira = Regex.Replace(jira, @"\[([^|]+?)\]", "<$1>");
            // Images
            jira = Regex.Replace(jira, @"!(.+)!", "![]($1)");
            // Named Links
            jira = Regex.Replace(jira, @"\[(.+?)\|(.+?)\]", "[$1]($2)");
            // Single Paragraph Blockquote
            jira = Regex.Replace(jira, @"^bq\.\s+", "> ", RegexOptions.Multiline);
            // Remove color: unsupported in md
            jira = new Regex(@"\{color:[^}]+\}(.*?)\{color\}", RegexOptions.Singleline, RegexTimeout).Replace(jira, "$1");
            // panel into table
            jira = new Regex(@"\{panel:title=([^}]*)\}\n?(.*?)\n?\{panel\}", RegexOptions.Singleline, RegexTimeout).Replace(jira,
                "\n| $1 |\n| --- |\n| $2 |");
            // table header
            jira = Regex.Replace(jira, @"^[ \t]*((?:\|\|.*?)+\|\|)[ \t]*$",
                match =>
                {
                    var singleBarred = Regex.Replace(match.Value, @"\|\|", "|");
                    return '\n' + singleBarred + '\n' + Regex.Replace(singleBarred, @"\|[^|]+", "| --- ");
                }, 
                RegexOptions.Multiline);
            // remove leading-space of table headers and rows
            jira = Regex.Replace(jira, @"^[ \t]*\|", "|");
            return jira;
        }

        public string Md2Html(string markdown)
        {
            var pipeline = new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .UseAutoLinks()
                .Build();
            
            return Markdown.ToHtml(markdown, pipeline);
        }

        private void checkBoxSource_CheckedChanged(object sender, EventArgs e)
        {
            textBoxIssue.Visible = checkBoxSource.Checked;
            webView21.Visible = !checkBoxSource.Checked;
        }

        private void buttonOpen_Click(object sender, EventArgs e)
        {
            OpenEntitiesXml();
        }

        // Encoding helpers for safe HTML generation
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

        private static string HtmlE(string s) =>
            WebUtility.HtmlEncode(s ?? string.Empty);

        private static string JsStringE(string s)
        {
            if (s is null) return string.Empty;
            return s.Replace("\\", "\\\\")
                    .Replace("'", "\\'")
                    .Replace("\"", "\\\"")
                    .Replace("/", "\\/")
                    .Replace("\b", "\\b")
                    .Replace("\f", "\\f")
                    .Replace("\t", "\\t")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n");
        }

        private void LoadApprovalsFromActiveObjects()
        {
            var activeObjectsPath = Path.Combine(basePath, "activeobjects.xml");
            if (!File.Exists(activeObjectsPath))
                return;

            XDocument activeObjectsDocument;
            try
            {
                var xml = File.ReadAllText(activeObjectsPath, Encoding.UTF8);
                xml = Regex.Replace(xml, "[\x00-\x08\x0B\x0C\x0E-\x1F]", "", RegexOptions.Compiled);
                xml = Regex.Replace(xml, @"&(?!([a-zA-Z][a-zA-Z0-9]*|#[0-9]+|#x[0-9a-fA-F]+);)", "&amp;", RegexOptions.Compiled);
                using var sr = new StringReader(xml);
                activeObjectsDocument = XDocument.Load(sr);
            }
            catch
            {
                return;
            }

            var approvals = activeObjectsDocument
                .Descendants()
                .Where(e => e.Name.LocalName.EndsWith("_APPROVAL", StringComparison.InvariantCultureIgnoreCase))
                .ToList();
            var approvers = activeObjectsDocument
                .Descendants()
                .Where(e => e.Name.LocalName.EndsWith("_APPROVER", StringComparison.InvariantCultureIgnoreCase))
                .ToList();

            if (!approvers.Any())
                return;

            var approvalToIssue = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

            foreach (var approval in approvals)
            {
                var approvalId = GetColumnValue(approval, "ID");
                var issueId = GetColumnValue(approval, "ISSUE_ID", "ISSUE", "REQUEST_ID");
                if (string.IsNullOrWhiteSpace(approvalId) || string.IsNullOrWhiteSpace(issueId))
                    continue;

                approvalToIssue[approvalId] = issueId;
            }

            foreach (var approver in approvers)
            {
                var issueId = GetColumnValue(approver, "ISSUE_ID", "ISSUE", "REQUEST_ID");
                var approvalId = GetColumnValue(approver, "APPROVAL_ID");
                if (string.IsNullOrWhiteSpace(issueId) && !string.IsNullOrWhiteSpace(approvalId))
                    approvalToIssue.TryGetValue(approvalId, out issueId);

                if (string.IsNullOrWhiteSpace(issueId) || !Issues.TryGetValue(issueId, out var issue))
                    continue;

                var approverName = ResolveUserDisplayName(GetColumnValue(approver,
                    "APPROVER_USER_KEY", "USER_KEY", "APPROVER", "USER", "USERNAME", "AUTHOR"));
                var role = GetColumnValue(approver, "APPROVER_ROLE", "ROLE");
                var approvedAt = ParseDateTimeNullable(GetColumnValue(approver,
                    "DECIDED_DATE", "APPROVED_DATE", "UPDATED", "UPDATED_DATE", "CREATED"));
                var status = GetColumnValue(approver, "DECISION", "STATUS", "RESPONSE");

                issue.Approvals.Add(new SearchApproval
                {
                    Approver = string.IsNullOrWhiteSpace(approverName) ? "(unknown)" : approverName,
                    Role = string.IsNullOrWhiteSpace(role) ? "Approver" : role,
                    ApprovedAt = approvedAt,
                    Status = string.IsNullOrWhiteSpace(status) ? "Pending" : status
                });
            }
        }

        private static DateTime? ParseDateTimeNullable(string value)
        {
            if (DateTime.TryParse(value, out var parsed))
                return parsed;
            return null;
        }

        private static string GetColumnValue(XElement row, params string[] names)
        {
            foreach (var name in names)
            {
                var attributeMatch = row.Attributes().FirstOrDefault(a =>
                    string.Equals(a.Name.LocalName, name, StringComparison.InvariantCultureIgnoreCase));
                if (!string.IsNullOrWhiteSpace(attributeMatch?.Value))
                    return attributeMatch.Value;

                var childMatch = row.Elements().FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, name, StringComparison.InvariantCultureIgnoreCase));
                if (!string.IsNullOrWhiteSpace(childMatch?.Value))
                    return childMatch.Value;

                var namedColumnMatch = row.Elements().FirstOrDefault(e =>
                    string.Equals(e.Attribute("name")?.Value, name, StringComparison.InvariantCultureIgnoreCase)
                    || string.Equals(e.Attribute("column")?.Value, name, StringComparison.InvariantCultureIgnoreCase));
                if (!string.IsNullOrWhiteSpace(namedColumnMatch?.Value))
                    return namedColumnMatch.Value;
            }

            return null;
        }

        private void AddUserMapping(string key, string displayName)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(displayName))
                return;

            Users[key] = displayName;
        }

        private string ResolveUserDisplayName(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return string.Empty;

            return Users.TryGetValue(key, out var displayName)
                ? displayName
                : key;
        }
    }
}
