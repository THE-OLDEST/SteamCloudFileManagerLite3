using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.IO;
using System.Text.RegularExpressions;

namespace SteamCloudFileManager
{
    public partial class MainForm : Form
    {
        IRemoteStorage storage;
        // Item1 = cloud name, Item2 = path on disk
        Queue<Tuple<string, string>> uploadQueue = new Queue<Tuple<string, string>>();

        public MainForm()
        {
            InitializeComponent();
        }
        public static string ShowDialog(string text, string caption)
        {
            Form prompt = new Form()
            {
                Width = 340,
                Height = 150,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                Text = caption,
                StartPosition = FormStartPosition.CenterScreen
            };
            //Label textLabel = new Label() { Left = 20, Top = 20, Width = 100, Text = text };
            Label textLabel = new Label();
            textLabel.Size = new Size(300, 50);
            textLabel.Text = text;
            textLabel.Left = 20;
            textLabel.Top = 20;
            TextBox textBox = new TextBox() { Left = 75, Top = 75, Width = 125 };
            Button confirmation = new Button() { Text = "Ok", Left = 200, Width = 60, Top = 75, DialogResult = DialogResult.OK };
            confirmation.Click += (sender, e) => { prompt.Close(); };
            prompt.Controls.Add(textBox);
            prompt.Controls.Add(confirmation);
            prompt.Controls.Add(textLabel);
            prompt.AcceptButton = confirmation;

            return prompt.ShowDialog() == DialogResult.OK ? textBox.Text : "";
        }
        private void connectButton_Click(object sender, EventArgs e)
        {
            try
            {
                uint appId;
                if (string.IsNullOrWhiteSpace(appIdTextBox.Text))
                {
                    MessageBox.Show(this, "Please enter an App ID.", "Failed to connect", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (!uint.TryParse(appIdTextBox.Text.Trim(), out appId))
                {
                    MessageBox.Show(this, "Please make sure the App ID you entered is valid.", "Failed to connect", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                storage = RemoteStorage.CreateInstance(uint.Parse(appIdTextBox.Text));
                //storage = new RemoteStorageLocal("remote", uint.Parse(appIdTextBox.Text));
                refreshButton.Enabled = true;
                uploadButton.Enabled = true;
                refreshButton_Click(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.ToString(), "Failed to connect", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void refreshButton_Click(object sender, EventArgs e)
        {
            if (storage == null)
            {
                MessageBox.Show(this, "Not connected", "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }

            try
            {
                List<IRemoteFile> files = storage.GetFiles();
                remoteListView.Items.Clear();
                foreach (IRemoteFile file in files)
                {
                    ListViewItem itm = new ListViewItem(new string[] { file.Name, file.Timestamp.ToString(), file.Size.ToString(), file.IsPersisted.ToString(), file.Exists.ToString() }) { Tag = file };
                    remoteListView.Items.Add(itm);
                }
                updateQuota();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Can't refresh." + Environment.NewLine + ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
        }

        void updateQuota()
        {
            if (storage == null) throw new InvalidOperationException("Not connected");
            ulong totalBytes, availBytes;
            storage.GetQuota(out totalBytes, out availBytes);
            quotaLabel.Text = string.Format("{0}/{1} bytes used", totalBytes - availBytes, totalBytes);
        }

        private void downloadButton_Click(object sender, EventArgs e)
        {
            if (storage == null)
            {
                MessageBox.Show(this, "Not connected", "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }
            if (remoteListView.SelectedIndices.Count != 1)
            {
                MessageBox.Show(this, "Please select only one file.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }

            IRemoteFile file = remoteListView.SelectedItems[0].Tag as IRemoteFile;
            saveFileDialog1.FileName = Path.GetFileName(file.Name);
            if (saveFileDialog1.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
            {
                try
                {
                    File.WriteAllBytes(saveFileDialog1.FileName, file.ReadAllBytes());
                    MessageBox.Show(this, "File downloaded.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "File download failed." + Environment.NewLine + ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
            }
        }

        private void deleteButton_Click(object sender, EventArgs e)
        {
            if (storage == null)
            {
                MessageBox.Show(this, "Not connected", "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }
            if (remoteListView.SelectedIndices.Count == 0)
            {
                MessageBox.Show(this, "Please select files to delete.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }

            if (MessageBox.Show(this, "Are you sure you want to delete the selected files?", "Confirm deletion", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == System.Windows.Forms.DialogResult.No) return;

            bool allSuccess = true;

            foreach (ListViewItem item in remoteListView.SelectedItems)
            {
                IRemoteFile file = item.Tag as IRemoteFile;
                try
                {
                    bool success = file.Delete();
                    if (!success)
                    {
                        allSuccess = false;
                        MessageBox.Show(this, file.Name + " failed to delete.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    }
                    else
                    {
                        item.Remove();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, file.Name + " failed to delete." + Environment.NewLine + ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
            }

            updateQuota();
            if (allSuccess) MessageBox.Show(this, "Files deleted.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void remoteListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            downloadButton.Enabled = deleteButton.Enabled = (storage != null && remoteListView.SelectedIndices.Count > 0);
        }

        private void uploadBackgroundWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker worker = (BackgroundWorker)sender;
            List<string> failedFiles = new List<string>();
            while (uploadQueue.Count > 0)
            {
                var uploadItem = uploadQueue.Dequeue();
                IRemoteFile file = storage.GetFile(uploadItem.Item1);
                try
                {
                    byte[] data = File.ReadAllBytes(uploadItem.Item2);
                    if (!file.WriteAllBytes(data))
                        failedFiles.Add(uploadItem.Item1);
                }
                catch (IOException ex)
                {
                    failedFiles.Add(uploadItem.Item1);
                }
            }

            e.Result = failedFiles;
        }

        private void uploadButton_Click(object sender, EventArgs e)
        {
            if (openFileDialog1.ShowDialog(this) == DialogResult.OK)
            {
                disableUploadGui();
                foreach (var selectedFile in openFileDialog1.FileNames)
                {
                    string name = Path.GetFileName(selectedFile).ToLowerInvariant();
                    string text = String.Format("Enter path for file {0} ex. \"somedir/anotherdir/\".\n Leave empty to upload to root folder", name);
                    string promptValue = ShowDialog(text, "Select Path to upload to");
                    if(!promptValue.EndsWith("/"))
                    {
                        promptValue = String.Format("{0}/",promptValue);
                    }
                    string namepath = String.Format("{0}{1}", promptValue,name);
                    uploadQueue.Enqueue(new Tuple<string, string>(namepath, selectedFile));
                }
                uploadBackgroundWorker.RunWorkerAsync();
            }
        }

        void disableUploadGui()
        {
            // Disables app switching, refresh, and upload button
            connectButton.Enabled = false;
            refreshButton.Enabled = false;
            uploadButton.Enabled = false;
            uploadButton.Text = "Uploading...";
        }

        void enableUploadGui()
        {
            connectButton.Enabled = true;
            refreshButton.Enabled = true;
            uploadButton.Enabled = true;
            uploadButton.Text = "Upload";
        }

        private void uploadBackgroundWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            var failedList = e.Result as List<string>;
            if (failedList.Count == 0)
            {
                MessageBox.Show(this, "Upload complete.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                failedList.Insert(0, "The following files have failed to upload:");
                MessageBox.Show(this, string.Join(Environment.NewLine, failedList), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            enableUploadGui();
            refreshButton_Click(this, EventArgs.Empty);
        }
    }
}
