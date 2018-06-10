using System;
using System.IO;
using System.Windows.Forms;

namespace FivemCacheDecoder.Gui
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            var folderSelectDialog = new FolderSelectDialog();
            folderSelectDialog.Title = "Cache directory";

            if (folderSelectDialog.Show()) {
                CacheDirBox.Text = folderSelectDialog.FileName;
            }
        }

        private void button2_Click(object sender, EventArgs e)
        {
            var folderSelectDialog = new FolderSelectDialog();
            folderSelectDialog.Title = "Output directory";

            if (folderSelectDialog.Show()) {
                OutputDirBox.Text = folderSelectDialog.FileName;
            }
        }

        private void button3_Click(object sender, EventArgs e)
        {
            textBox1.Text = "Starting decryption";

            var opt = new FivemCacheDecoder.Program.DecodeOptions();
            opt.CacheDirectory = CacheDirBox.Text;
            opt.Duplicates = !SkipDupsBox.Checked;
            opt.OutputDirectory = Path.Combine(OutputDirBox.Text, OutputPatternBox.Text);
            var tw = new StringWriter();
            Console.SetOut(tw);
            try
            {
                FivemCacheDecoder.Program.DecodeVerb(opt);
                tw.Close();
                textBox1.Text = tw.ToString();
            }
            catch (Exception ex)
            {
                tw.Close();
                textBox1.Text = tw.ToString() + "\n" + ex.ToString();
            }

            var standardOutput = new StreamWriter(Console.OpenStandardOutput());
            standardOutput.AutoFlush = true;
            Console.SetOut(standardOutput);
        }

        private void button4_Click(object sender, EventArgs e)
        {
            textBox1.Text = "Starting encryption";

            var opt = new FivemCacheDecoder.Program.EncodeOptions();
            opt.CacheDirectory = CacheDirBox.Text;
            opt.OutputDirectory = Path.Combine(OutputDirBox.Text, OutputPatternBox.Text);
            opt.DryRun = DryRunBox.Checked;

            var tw = new StringWriter();
            Console.SetOut(tw);
            try
            {
                FivemCacheDecoder.Program.EncodeVerb(opt);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
            finally
            {
                tw.Close();
                textBox1.Text = tw.ToString();
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
        }
    }
}
