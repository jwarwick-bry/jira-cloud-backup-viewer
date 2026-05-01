using Markdig;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
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
            = new Dictionary<string, string>();

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
        }

        private string basePath;

        private void LoadFile(string filename)
        {
            toolStripStatusLabel1.Text = $"Loading {filename}, please wait..";
            Application.DoEvents();

            basePath = Path.GetDirectoryName(filename);
            webView21.CoreWebView2.SetVirtualHostNameToFolderMapping("attachment.path", $"{basePath}\\data\\attachments", Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);            

            //Fix invalid chars and load Xml
            var s = File.ReadAllText(filename, System.Text.Encoding.UTF8);
            // Remove invalid XML control characters (excluding tab \x09, LF \x0A, CR \x0D)
            s = Regex.Replace(s, "[\x00-\x08\x0B\x0C\x0E-\x1F]", "", RegexOptions.Compiled);
            // Replace bare & not part of a valid XML entity reference with &amp;
            s = Regex.Replace(s, @"&(?!([a-zA-Z][a-zA-Z0-9]*|#[0-9]+|#x[0-9a-fA-F]+);)", "&amp;", RegexOptions.Compiled);
            using (var sr = new StringReader(s))
                XDocument = XDocument.Load(sr);

            Issues = XDocument.Descendants("Issue").ToDictionary(i => i.Attribute("id").Value, i => new SearchIssue { Issue = i });
            Users = XDocument.Descendants("User").ToDictionary(i => i.Attribute("userName").Value, i => i.Attribute("displayName").Value);

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
                        var attachUrl = $"http://attachment.path/{UrlPathE(si.ProjectKey)}/10000/{UrlPathE(si.IssueNr)}/{UrlPathE(fa.Id)}";
                        var htmlUrl = HtmlE(attachUrl);
                        var htmlFilename = HtmlE(fa.Filename);
                        var jsFilename = HtmlE(JsStringE(fa.Filename));
                        return $@"<strong>{htmlFilename}</strong>
                        <a target='attachmentWindow' onclick=""window.open('{htmlUrl}','attachmentWindow'); return false;"" href='{htmlUrl}' download='{htmlFilename}'>preview</a>
                        <a target='attachmentWindow' onclick=""openfordownload('{htmlUrl}','{jsFilename}'); return false;"" href='{htmlUrl}' download='{htmlFilename}'>download</a>
                        <br />";
                    })));
                sections.Add(Md2Html(Jira2Md(textBoxIssue.Text ?? string.Empty)));
                sections.AddRange(si.Actions.OrderBy(a => a.Created).Select(a =>
                {
                    var displayName = Users.ContainsKey(a.Author) ? Users[a.Author] : a.Author;
                    return $"<h3 class='jcv';>{HtmlE(a.Type)} - {a.Created} - {HtmlE(displayName)}</h3>\n    {Md2Html(Jira2Md(a.Body ?? string.Empty))}";
                }));
                webView21.NavigateToString(@$"<style>body {{ font-family: sans-serif; }} h3.jcv {{
    color:navy;
    background-color: rgba(0,0,100,0.05);
    padding: 5px;font-family: monospace;
    border: 1px solid navy;
    border-style: solid none;
}}</style>
<script>
function openfordownload(url, filename){{
    fetch(url)
        .then(resp => resp.blob())
        .then(blobobject => {{
            const blob = window.URL.createObjectURL(blobobject);
            const anchor = document.createElement('a');
            anchor.style.display = 'none';
            anchor.href = blob;
            anchor.download = filename;
            anchor.target = 'attachmentWindow';
            document.body.appendChild(anchor);
            anchor.click();
            document.body.removeChild(anchor);
            window.URL.revokeObjectURL(blob);
        }})
}}

function preview(url, filename) {{
var w = window.open('','attachmentWindow');
    var downloadLink = document.createElement('a');
    downloadLink.href = url;
    downloadLink.download = filename
    downloadLink.target = 'attachmentWindow';
    document.body.appendChild(downloadLink);
    downloadLink.click();
    document.body.removeChild(downloadLink);
}}
</script>

                    <section><h2 style='color:navy;'>{HtmlE(si.IssueNr)} - {si.Created} - {HtmlE(si.Summary)}</h2>
                    {string.Join($"</section><section>", sections)}</section>"); 

                textBoxIssue.Text = textBoxIssue.Text.Replace("\r", "").Replace("\n", "\r\n");
            }
            else
                dataGridView2.DataSource = null;
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

        private static string UrlPathE(string s) =>
            Uri.EscapeDataString(s ?? string.Empty);

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
    }
}
