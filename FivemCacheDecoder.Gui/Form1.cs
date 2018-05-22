using System;
using System.IO;
using System.Linq;
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
            using (var ofd = new FolderBrowserDialog())
                if (ofd.ShowDialog() == DialogResult.OK)
                    CacheDirBox.Text = ofd.SelectedPath;
                
        }

        private void button2_Click(object sender, EventArgs e)
        {
            using (var ofd = new FolderBrowserDialog())
                if (ofd.ShowDialog() == DialogResult.OK)
                    OutputDirBox.Text = ofd.SelectedPath;

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
    }
}
