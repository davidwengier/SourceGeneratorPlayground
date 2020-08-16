using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SourceGeneratorPlayground
{
    public partial class MainForm : Form
    {
        private const int ErrorListHeight = 200;

        public MainForm()
        {
            this.StartPosition = FormStartPosition.WindowsDefaultBounds;
            this.Text = "Source Generator Playground - v" + ThisAssembly.AssemblyInformationalVersion;

            var splitter = new SplitContainer()
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                IsSplitterFixed = true
            };
            this.Controls.Add(splitter);

            TextBox? codeEditor = CreateTextBox(false);
            TextBox? generator = CreateTextBox(false);

            TextBox? generatorOutput = CreateTextBox(true);
            TextBox? programOutput = CreateTextBox(true);
            TextBox? errors = CreateTextBox(true);

            var outputSplitter = new SplitContainer()
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                IsSplitterFixed = true
            };
            outputSplitter.Panel1.Controls.Add(CreateTabs("Generator Output", generatorOutput, "Program Output", programOutput));
            outputSplitter.Panel2.Controls.Add(errors);

            splitter.Panel1.Controls.Add(CreateTabs("Code", codeEditor, "Generator", generator));
            splitter.Panel2.Controls.Add(outputSplitter);

            Resize += ResizeControls;
            Load += ResizeControls;

            codeEditor.TextChanged += Generate;
            generator.TextChanged += Generate;

            this.MainMenuStrip = new MenuStrip();
            this.MainMenuStrip.Dock = DockStyle.Top;
            this.Controls.Add(this.MainMenuStrip);
            var loadMenu = (ToolStripMenuItem)this.MainMenuStrip.Items.Add("Load");
            foreach (var name in GetType().Assembly.GetManifestResourceNames())
            {
                if (name.StartsWith("SourceGeneratorPlayground.Samples") && name.EndsWith(".Generator.cs"))
                {
                    loadMenu.DropDownItems.Add(name.Split(".")[2]).Click += LoadSample;
                }
            }
            loadMenu.DropDownItems[2].PerformClick();
            this.MainMenuStrip.Visible = true;

            void LoadSample(object? s, EventArgs e)
            {
                string name = ((ToolStripItem)s).Text;
                using var streamReader = new StreamReader(GetType().Assembly.GetManifestResourceStream(nameof(SourceGeneratorPlayground) + ".Samples." + name + ".Program.cs")!);
                codeEditor.Text = streamReader.ReadToEnd();
                using var streamReader1 = new StreamReader(GetType().Assembly.GetManifestResourceStream(nameof(SourceGeneratorPlayground) + ".Samples." + name + ".Generator.cs")!);
                generator.Text = streamReader1.ReadToEnd();
            }

            void Generate(object? s, EventArgs e)
            {
                using var runner = new Runner(codeEditor.Text, generator.Text);
                runner.Run();
                generatorOutput.Text = runner.GeneratorOutput;
                programOutput.Text = runner.ProgramOutput;
                errors.Text = runner.ErrorText;
                outputSplitter.Panel2Collapsed = string.IsNullOrWhiteSpace(runner.ErrorText);
            }

            void ResizeControls(object? s, EventArgs e)
            {
                if (outputSplitter.Height <= ErrorListHeight) return;

                splitter.SplitterDistance = splitter.Width / 2;
                outputSplitter.SplitterDistance = outputSplitter.Height - ErrorListHeight;
            }
        }

        private static TabControl CreateTabs(string tabOneTitle, Control tablOneControl, string tabTwoTitle, Control tabTwoControl)
        {
            var tabs = new TabControl()
            {
                Dock = DockStyle.Fill
            };
            tabs.TabPages.Add(tabOneTitle);
            tabs.TabPages.Add(tabTwoTitle);
            tabs.SelectedIndexChanged += (s, e) => tabs.SelectedTab.Controls[0].Focus();

            tabs.TabPages[0].Controls.Add(tablOneControl);
            tabs.TabPages[1].Controls.Add(tabTwoControl);

            return tabs;
        }

        private static TextBox CreateTextBox(bool isReadOnly)
            => new TextBox()
            {
                Dock = DockStyle.Fill,
                Font = new Font("Cascadia Code", 11),
                Multiline = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                BackColor = SystemColors.Window,
                ReadOnly = isReadOnly,
                WordWrap = false,
                ScrollBars = ScrollBars.Both
            };
    }
}
