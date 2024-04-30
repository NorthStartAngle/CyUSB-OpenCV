using System;
using System.Drawing;
using System.Collections;
using System.ComponentModel;
using System.Windows.Forms;
using System.Data;
using System.Threading;
using CyUSB;
using System.Runtime.InteropServices;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using KGySoft.Drawing;
using System.Text;
using java.awt.@event;
using java.awt;
using System.Diagnostics;
using Label = System.Windows.Forms.Label;
using Button = System.Windows.Forms.Button;
using Color = System.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;
using OpenCvSharp;
using Mat = OpenCvSharp.Mat;
using Image = System.Drawing.Image;
using Emgu.CV.Structure;
using Emgu.CV;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Window;
using Emgu.CV.Util;
using javax.xml.crypto;
using System.Threading.Tasks;
using System.Numerics;
using KGySoft.CoreLibraries;
using Emgu.CV.CvEnum;

namespace Streamer
{

	public class Form1 : System.Windows.Forms.Form
	{
		bool bVista;

		USBDeviceList usbDevices;
		CyUSBDevice MyDevice;
		CyUSBEndPoint EndPoint;

		byte[] sample = new byte[360960];
		Bitmap bmp;

		CyControlEndPoint CtrlEndPt = null;
		string configFile = Application.StartupPath + "\\config\\sensor.conf";

		//Vendor Commands
		const byte VX_AA = 0xAA;                //Init Vendor Command
		const byte VX_FE = 0xFE;                //Reg Vendor Command
		const byte VX_F5 = 0xF5;                //Start streaming Vendor Command
		const byte VX_FC = 0xFC;                //Lense switch between dark and Light Vendor Command
		const byte VX_F3 = 0xF3;                //Get 32 byte from device Vendor Command
		const byte VX_F4 = 0xF4;                //Set 32 byte to device Vendor Command

		byte[] gbl_buffer;                      //Frame Buffer = Ht*Wd
		static int Ht = 480;                    //int Ht = 480;
		static int Wd = 752;                    //int Wd = 752;
		static int bytes = Wd * Ht;

		//Header        
		byte[] buffer = new byte[bytes];
		int intialByte = 512;                   //512 bytes; the header 5 bytes are part of a bigger 512 bytes
		bool IsPkt = false;

		DateTime t1, t2;
		TimeSpan elapsed;
		double XferBytes;
		long xferRate;
		static byte DefaultBufInitValue = 0xA5;//VX_F5;//0xA5;


        int BufSz;
		int QueueSz;
		int PPX;
		int IsoPktBlockSize;
		int Successes;
		int Failures;

		Thread tListen;
		static bool bRunning;

		// These are  needed for Thread to update the UI
		delegate void UpdateUICallback();
		UpdateUICallback updateUI;
		private Label label6;
		private ComboBox DevicesComboBox;
		private PictureBox SensorView;
		private Button button1;
		private Button button2;

		// These are needed to close the app from the Thread exception(exception handling)
		delegate void ExceptionCallback();
		ExceptionCallback handleException;
        private Label label7;
        private Button button3;
        private TextBox txtQueue;
        private TextBox txtPPX;
        private TextBox txtVBlank;
        BitmapData bmpData;


        public Form1()
		{
			bVista = (Environment.OSVersion.Version.Major < 6) ||
				((Environment.OSVersion.Version.Major == 6) && Environment.OSVersion.Version.Minor == 0);

			// Required for Windows Form Designer support
			InitializeComponent();

			// Setup the callback routine for updating the UI
			updateUI = new UpdateUICallback(StatusUpdate);

			// Setup the callback routine for NullReference exception handling
			handleException = new ExceptionCallback(ThreadException);

			// Create the list of USB devices attached to the CyUSB3.sys driver.
			usbDevices = new USBDeviceList(CyConst.DEVICES_CYUSB);

			//Assign event handlers for device attachment and device removal.
			usbDevices.DeviceAttached += new EventHandler(usbDevices_DeviceAttached);
			usbDevices.DeviceRemoved += new EventHandler(usbDevices_DeviceRemoved);

			//Set and search the device with VID-PID 04b4-1003 and if found, selects the end point
			SetDevice(false);
		}


		/*Summary
		   This is the event handler for device removal. This method resets the device count and searches for the device with 
		   VID-PID 04b4-1003
		*/
		void usbDevices_DeviceRemoved(object sender, EventArgs e)
		{
			bRunning = false;

			if (tListen != null && tListen.IsAlive == true)
			{
				tListen.Abort();
				tListen.Join();
				tListen = null;
			}

			MyDevice = null;
			EndPoint = null;
			SetDevice(false);

			if (StartBtn.Text.Equals("Start") == false)
			{
				{
					DevicesComboBox.Enabled = true;
					EndPointsComboBox.Enabled = true;
					PpxBox.Enabled = true;
					QueueBox.Enabled = true;
					StartBtn.Text = "Start";
					bRunning = false;

					t2 = DateTime.Now;
					elapsed = t2 - t1;
					xferRate = (long)(XferBytes / elapsed.TotalMilliseconds);
					xferRate = xferRate / (int)100 * (int)100;

					StartBtn.BackColor = System.Drawing.Color.Aquamarine;
				}

			}
		}



		/*Summary
		   This is the event handler for device attachment. This method  searches for the device with 
		   VID-PID 04b4-00F1
		*/
		void usbDevices_DeviceAttached(object sender, EventArgs e)
		{
			SetDevice(false);
		}



		/*Summary
		   Search the device with VID-PID 04b4-00F1 and if found, select the end point
		*/
		private void SetDevice(bool bPreserveSelectedDevice)
		{
			int nCurSelection = 0;
			if (DevicesComboBox.Items.Count > 0)
			{
				nCurSelection = DevicesComboBox.SelectedIndex;
				DevicesComboBox.Items.Clear();
			}
			int nDeviceList = usbDevices.Count;
			for (int nCount = 0; nCount < nDeviceList; nCount++)
			{
				USBDevice fxDevice = usbDevices[nCount];
				String strmsg;
				strmsg = "(0x" + fxDevice.VendorID.ToString("X4") + " - 0x" + fxDevice.ProductID.ToString("X4") + ") " + fxDevice.FriendlyName;
				DevicesComboBox.Items.Add(strmsg);
			}

			if (DevicesComboBox.Items.Count > 0)
				DevicesComboBox.SelectedIndex = ((bPreserveSelectedDevice == true) ? nCurSelection : 0);

			USBDevice dev = usbDevices[DevicesComboBox.SelectedIndex];

			if (dev != null)
			{
				MyDevice = (CyUSBDevice)dev;

				GetEndpointsOfNode(MyDevice.Tree);
				PpxBox.Text = ""; //Set default value to 8 Packets
				QueueBox.Text = "";
				if (EndPointsComboBox.Items.Count > 0)
				{
					EndPointsComboBox.SelectedIndex = 0;
					StartBtn.Enabled = true;
					//start();
				}
				else StartBtn.Enabled = false;

				Text = MyDevice.FriendlyName;
			}
			else
			{
				StartBtn.Enabled = false;
				EndPointsComboBox.Items.Clear();
				EndPointsComboBox.Text = "";
				Text = "C# Streamer - no device";
			}
		}



		/*Summary
		   Recursive routine populates EndPointsComboBox with strings 
		   representing all the endpoints in the device.
		*/
		private void GetEndpointsOfNode(TreeNode devTree)
		{
			//EndPointsComboBox.Items.Clear();
			foreach (TreeNode node in devTree.Nodes)
			{
				if (node.Nodes.Count > 0)
					GetEndpointsOfNode(node);
				else
				{
					CyUSBEndPoint ept = node.Tag as CyUSBEndPoint;
					if (ept == null)
					{
						//return;
					}
					else if (!node.Text.Contains("Control"))
					{
						CyUSBInterface ifc = node.Parent.Tag as CyUSBInterface;
						string s = string.Format("ALT-{0}, {1} Byte {2}", ifc.bAlternateSetting, ept.MaxPktSize, node.Text);
						EndPointsComboBox.Items.Add(s);
					}

				}
			}

		}




		/*Summary
		   Clean up any resources being used.
		*/
		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				if (components != null)
				{
					components.Dispose();
				}
			}
			base.Dispose(disposing);
		}


		#region Windows Form Designer generated code

		private System.Windows.Forms.MainMenu mainMenu;
		private System.Windows.Forms.MenuItem menuItem1;
		private System.Windows.Forms.MenuItem menuItem2;
		private System.Windows.Forms.MenuItem ExitItem;
		private System.Windows.Forms.MenuItem menuItem3;
		private System.Windows.Forms.MenuItem AboutItem;
		private System.Windows.Forms.Label label1;
		private System.Windows.Forms.Label label2;
		private System.Windows.Forms.Label label3;
		private System.Windows.Forms.Label label4;
		private System.Windows.Forms.ComboBox PpxBox;
		private System.Windows.Forms.ComboBox QueueBox;
		private System.Windows.Forms.TextBox SuccessBox;
		private System.Windows.Forms.GroupBox groupBox1;
		private System.Windows.Forms.ProgressBar ProgressBar;
		private System.Windows.Forms.Button StartBtn;
		private System.Windows.Forms.Label ThroughputLabel;
		private System.Windows.Forms.TextBox FailuresBox;
		private ComboBox EndPointsComboBox;
		private Label label5;

		private IContainer components;

		/// <summary>
		/// Required method for Designer support - do not modify
		/// the contents of this method with the code editor.
		/// </summary>
		private void InitializeComponent()
		{
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            this.mainMenu = new System.Windows.Forms.MainMenu(this.components);
            this.menuItem1 = new System.Windows.Forms.MenuItem();
            this.menuItem2 = new System.Windows.Forms.MenuItem();
            this.ExitItem = new System.Windows.Forms.MenuItem();
            this.menuItem3 = new System.Windows.Forms.MenuItem();
            this.AboutItem = new System.Windows.Forms.MenuItem();
            this.label1 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.label3 = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.PpxBox = new System.Windows.Forms.ComboBox();
            this.QueueBox = new System.Windows.Forms.ComboBox();
            this.SuccessBox = new System.Windows.Forms.TextBox();
            this.FailuresBox = new System.Windows.Forms.TextBox();
            this.groupBox1 = new System.Windows.Forms.GroupBox();
            this.ThroughputLabel = new System.Windows.Forms.Label();
            this.ProgressBar = new System.Windows.Forms.ProgressBar();
            this.StartBtn = new System.Windows.Forms.Button();
            this.EndPointsComboBox = new System.Windows.Forms.ComboBox();
            this.label5 = new System.Windows.Forms.Label();
            this.label6 = new System.Windows.Forms.Label();
            this.DevicesComboBox = new System.Windows.Forms.ComboBox();
            this.SensorView = new System.Windows.Forms.PictureBox();
            this.button1 = new System.Windows.Forms.Button();
            this.button2 = new System.Windows.Forms.Button();
            this.label7 = new System.Windows.Forms.Label();
            this.button3 = new System.Windows.Forms.Button();
            this.txtQueue = new System.Windows.Forms.TextBox();
            this.txtPPX = new System.Windows.Forms.TextBox();
            this.txtVBlank = new System.Windows.Forms.TextBox();
            this.groupBox1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.SensorView)).BeginInit();
            this.SuspendLayout();
            // 
            // mainMenu
            // 
            this.mainMenu.MenuItems.AddRange(new System.Windows.Forms.MenuItem[] {
            this.menuItem1,
            this.menuItem3});
            // 
            // menuItem1
            // 
            this.menuItem1.Index = 0;
            this.menuItem1.MenuItems.AddRange(new System.Windows.Forms.MenuItem[] {
            this.menuItem2,
            this.ExitItem});
            this.menuItem1.Text = "File";
            // 
            // menuItem2
            // 
            this.menuItem2.Index = 0;
            this.menuItem2.Text = "-";
            // 
            // ExitItem
            // 
            this.ExitItem.Index = 1;
            this.ExitItem.Text = "Exit";
            this.ExitItem.Click += new System.EventHandler(this.ExitItem_Click);
            // 
            // menuItem3
            // 
            this.menuItem3.Index = 1;
            this.menuItem3.MenuItems.AddRange(new System.Windows.Forms.MenuItem[] {
            this.AboutItem});
            this.menuItem3.Text = "Help";
            this.menuItem3.Visible = false;
            // 
            // AboutItem
            // 
            this.AboutItem.Index = 0;
            this.AboutItem.Text = "About";
            this.AboutItem.Click += new System.EventHandler(this.AboutItem_Click);
            // 
            // label1
            // 
            this.label1.Location = new System.Drawing.Point(17, 102);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(89, 16);
            this.label1.TabIndex = 0;
            this.label1.Text = "Packets per Xfer";
            this.label1.TextAlign = System.Drawing.ContentAlignment.BottomLeft;
            // 
            // label2
            // 
            this.label2.Location = new System.Drawing.Point(17, 141);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(89, 17);
            this.label2.TabIndex = 1;
            this.label2.Text = "Xfers to Queue";
            this.label2.TextAlign = System.Drawing.ContentAlignment.BottomLeft;
            // 
            // label3
            // 
            this.label3.Location = new System.Drawing.Point(216, 102);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(64, 17);
            this.label3.TabIndex = 2;
            this.label3.Text = "Successes";
            this.label3.TextAlign = System.Drawing.ContentAlignment.BottomLeft;
            this.label3.Visible = false;
            // 
            // label4
            // 
            this.label4.Location = new System.Drawing.Point(191, 433);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(142, 18);
            this.label4.TabIndex = 3;
            this.label4.Text = "Failures";
            this.label4.TextAlign = System.Drawing.ContentAlignment.BottomLeft;
            // 
            // PpxBox
            // 
            this.PpxBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.PpxBox.Items.AddRange(new object[] {
            "1",
            "2",
            "4",
            "8",
            "16",
            "32",
            "64",
            "128",
            "256",
            "512"});
            this.PpxBox.Location = new System.Drawing.Point(115, 102);
            this.PpxBox.Name = "PpxBox";
            this.PpxBox.Size = new System.Drawing.Size(64, 21);
            this.PpxBox.TabIndex = 1;
            this.PpxBox.SelectedIndexChanged += new System.EventHandler(this.PpxBox_SelectedIndexChanged);
            // 
            // QueueBox
            // 
            this.QueueBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.QueueBox.Items.AddRange(new object[] {
            "1",
            "2",
            "4",
            "8",
            "16",
            "32",
            "64",
            "128"});
            this.QueueBox.Location = new System.Drawing.Point(115, 141);
            this.QueueBox.Name = "QueueBox";
            this.QueueBox.Size = new System.Drawing.Size(64, 21);
            this.QueueBox.TabIndex = 2;
            // 
            // SuccessBox
            // 
            this.SuccessBox.Location = new System.Drawing.Point(283, 102);
            this.SuccessBox.Name = "SuccessBox";
            this.SuccessBox.Size = new System.Drawing.Size(72, 20);
            this.SuccessBox.TabIndex = 6;
            this.SuccessBox.TabStop = false;
            this.SuccessBox.Text = "18";
            this.SuccessBox.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;
            this.SuccessBox.Visible = false;
            // 
            // FailuresBox
            // 
            this.FailuresBox.Location = new System.Drawing.Point(283, 142);
            this.FailuresBox.Name = "FailuresBox";
            this.FailuresBox.Size = new System.Drawing.Size(72, 20);
            this.FailuresBox.TabIndex = 7;
            this.FailuresBox.TabStop = false;
            this.FailuresBox.Text = "2";
            this.FailuresBox.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;
            this.FailuresBox.Visible = false;
            // 
            // groupBox1
            // 
            this.groupBox1.Controls.Add(this.ThroughputLabel);
            this.groupBox1.Controls.Add(this.ProgressBar);
            this.groupBox1.Location = new System.Drawing.Point(7, 298);
            this.groupBox1.Name = "groupBox1";
            this.groupBox1.Size = new System.Drawing.Size(337, 60);
            this.groupBox1.TabIndex = 8;
            this.groupBox1.TabStop = false;
            this.groupBox1.Text = " Throughput (KBps) ";
            this.groupBox1.Visible = false;
            // 
            // ThroughputLabel
            // 
            this.ThroughputLabel.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.ThroughputLabel.Location = new System.Drawing.Point(114, 38);
            this.ThroughputLabel.Name = "ThroughputLabel";
            this.ThroughputLabel.Size = new System.Drawing.Size(100, 16);
            this.ThroughputLabel.TabIndex = 1;
            this.ThroughputLabel.Text = "0";
            this.ThroughputLabel.TextAlign = System.Drawing.ContentAlignment.BottomCenter;
            // 
            // ProgressBar
            // 
            this.ProgressBar.ForeColor = System.Drawing.SystemColors.HotTrack;
            this.ProgressBar.Location = new System.Drawing.Point(16, 25);
            this.ProgressBar.Maximum = 500000;
            this.ProgressBar.Name = "ProgressBar";
            this.ProgressBar.Size = new System.Drawing.Size(294, 10);
            this.ProgressBar.TabIndex = 0;
            // 
            // StartBtn
            // 
            this.StartBtn.BackColor = System.Drawing.Color.Black;
            this.StartBtn.FlatStyle = System.Windows.Forms.FlatStyle.Popup;
            this.StartBtn.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.StartBtn.ForeColor = System.Drawing.SystemColors.ButtonHighlight;
            this.StartBtn.Location = new System.Drawing.Point(244, 68);
            this.StartBtn.Name = "StartBtn";
            this.StartBtn.Size = new System.Drawing.Size(127, 31);
            this.StartBtn.TabIndex = 3;
            this.StartBtn.Text = "Start";
            this.StartBtn.UseVisualStyleBackColor = false;
            this.StartBtn.Click += new System.EventHandler(this.StartBtn_Click);
            // 
            // EndPointsComboBox
            // 
            this.EndPointsComboBox.DropDownHeight = 120;
            this.EndPointsComboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.EndPointsComboBox.FormattingEnabled = true;
            this.EndPointsComboBox.IntegralHeight = false;
            this.EndPointsComboBox.Location = new System.Drawing.Point(115, 63);
            this.EndPointsComboBox.Name = "EndPointsComboBox";
            this.EndPointsComboBox.Size = new System.Drawing.Size(240, 21);
            this.EndPointsComboBox.TabIndex = 0;
            this.EndPointsComboBox.Visible = false;
            this.EndPointsComboBox.SelectedIndexChanged += new System.EventHandler(this.EndPointsComboBox_SelectedIndexChanged);
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(17, 66);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(94, 13);
            this.label5.TabIndex = 11;
            this.label5.Text = "Endpoint . . . . . . . ";
            this.label5.Visible = false;
            // 
            // label6
            // 
            this.label6.AutoSize = true;
            this.label6.Location = new System.Drawing.Point(17, 26);
            this.label6.Name = "label6";
            this.label6.Size = new System.Drawing.Size(96, 13);
            this.label6.TabIndex = 13;
            this.label6.Text = "Device Connected";
            this.label6.Visible = false;
            // 
            // DevicesComboBox
            // 
            this.DevicesComboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.DevicesComboBox.FormattingEnabled = true;
            this.DevicesComboBox.Location = new System.Drawing.Point(115, 22);
            this.DevicesComboBox.Name = "DevicesComboBox";
            this.DevicesComboBox.Size = new System.Drawing.Size(240, 21);
            this.DevicesComboBox.TabIndex = 14;
            this.DevicesComboBox.Visible = false;
            this.DevicesComboBox.SelectionChangeCommitted += new System.EventHandler(this.DeviceComboBox_SelectedIndexChanged);
            // 
            // SensorView
            // 
            this.SensorView.Location = new System.Drawing.Point(395, 4);
            this.SensorView.Margin = new System.Windows.Forms.Padding(6);
            this.SensorView.Name = "SensorView";
            this.SensorView.Size = new System.Drawing.Size(656, 461);
            this.SensorView.SizeMode = System.Windows.Forms.PictureBoxSizeMode.StretchImage;
            this.SensorView.TabIndex = 15;
            this.SensorView.TabStop = false;
            // 
            // button1
            // 
            this.button1.BackColor = System.Drawing.Color.Aquamarine;
            this.button1.Location = new System.Drawing.Point(87, 180);
            this.button1.Name = "button1";
            this.button1.Size = new System.Drawing.Size(128, 31);
            this.button1.TabIndex = 16;
            this.button1.Text = "Lens Switch";
            this.button1.UseVisualStyleBackColor = false;
            this.button1.Click += new System.EventHandler(this.button1_Click);
            // 
            // button2
            // 
            this.button2.BackColor = System.Drawing.Color.Black;
            this.button2.FlatStyle = System.Windows.Forms.FlatStyle.Popup;
            this.button2.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.button2.ForeColor = System.Drawing.SystemColors.ButtonHighlight;
            this.button2.Location = new System.Drawing.Point(72, 68);
            this.button2.Name = "button2";
            this.button2.Size = new System.Drawing.Size(128, 31);
            this.button2.TabIndex = 17;
            this.button2.Text = "Caliberate";
            this.button2.UseVisualStyleBackColor = false;
            this.button2.Click += new System.EventHandler(this.button2_Click);
            // 
            // label7
            // 
            this.label7.Location = new System.Drawing.Point(191, 399);
            this.label7.Name = "label7";
            this.label7.Size = new System.Drawing.Size(89, 17);
            this.label7.TabIndex = 18;
            this.label7.Text = "Xfers to Queue";
            this.label7.TextAlign = System.Drawing.ContentAlignment.BottomLeft;
            // 
            // button3
            // 
            this.button3.BackColor = System.Drawing.Color.Black;
            this.button3.FlatStyle = System.Windows.Forms.FlatStyle.Popup;
            this.button3.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.button3.ForeColor = System.Drawing.SystemColors.ButtonHighlight;
            this.button3.Location = new System.Drawing.Point(219, 219);
            this.button3.Name = "button3";
            this.button3.Size = new System.Drawing.Size(128, 29);
            this.button3.TabIndex = 19;
            this.button3.Text = "V Blank";
            this.button3.UseVisualStyleBackColor = false;
            this.button3.Click += new System.EventHandler(this.button3_Click);
            // 
            // txtQueue
            // 
            this.txtQueue.Location = new System.Drawing.Point(202, 143);
            this.txtQueue.Name = "txtQueue";
            this.txtQueue.Size = new System.Drawing.Size(72, 20);
            this.txtQueue.TabIndex = 21;
            this.txtQueue.TabStop = false;
            this.txtQueue.Text = "2";
            this.txtQueue.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;
            // 
            // txtPPX
            // 
            this.txtPPX.Location = new System.Drawing.Point(202, 103);
            this.txtPPX.Name = "txtPPX";
            this.txtPPX.Size = new System.Drawing.Size(72, 20);
            this.txtPPX.TabIndex = 20;
            this.txtPPX.TabStop = false;
            this.txtPPX.Text = "16";
            this.txtPPX.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;
            // 
            // txtVBlank
            // 
            this.txtVBlank.Location = new System.Drawing.Point(244, 186);
            this.txtVBlank.Name = "txtVBlank";
            this.txtVBlank.Size = new System.Drawing.Size(73, 20);
            this.txtVBlank.TabIndex = 22;
            this.txtVBlank.TabStop = false;
            this.txtVBlank.Text = "39";
            this.txtVBlank.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;
            // 
            // Form1
            // 
            this.AutoScaleBaseSize = new System.Drawing.Size(5, 13);
            this.ClientSize = new System.Drawing.Size(1921, 803);
            this.Controls.Add(this.txtVBlank);
            this.Controls.Add(this.txtQueue);
            this.Controls.Add(this.txtPPX);
            this.Controls.Add(this.button3);
            this.Controls.Add(this.label7);
            this.Controls.Add(this.button2);
            this.Controls.Add(this.button1);
            this.Controls.Add(this.StartBtn);
            this.Controls.Add(this.SensorView);
            this.Controls.Add(this.DevicesComboBox);
            this.Controls.Add(this.label6);
            this.Controls.Add(this.label5);
            this.Controls.Add(this.EndPointsComboBox);
            this.Controls.Add(this.groupBox1);
            this.Controls.Add(this.FailuresBox);
            this.Controls.Add(this.SuccessBox);
            this.Controls.Add(this.QueueBox);
            this.Controls.Add(this.PpxBox);
            this.Controls.Add(this.label4);
            this.Controls.Add(this.label3);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.label1);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.Fixed3D;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.KeyPreview = true;
            this.Menu = this.mainMenu;
            this.Name = "Form1";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Roombr Touch";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.Form1_FormClosing);
            this.Load += new System.EventHandler(this.Form1_Load);
            this.KeyDown += new System.Windows.Forms.KeyEventHandler(this.Form1_KeyDown);
            this.groupBox1.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.SensorView)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

		}
		#endregion


		/*Summary
		   Executes on clicking Help->about
		*/
		private void AboutItem_Click(object sender, System.EventArgs e)
		{
			string assemblyList = Util.Assemblies;
			MessageBox.Show(assemblyList, Text);
		}



		/*Summary
		   Executes on clicking File->Exit
		*/
		private void ExitItem_Click(object sender, System.EventArgs e)
		{
			Close();
		}

		private void Form1_Load(object sender, System.EventArgs e)
		{
			if (EndPointsComboBox.Items.Count > 0)
				EndPointsComboBox.SelectedIndex = 0;

            bmp = new Bitmap(Wd, Ht, PixelFormat.Format8bppIndexed);

            ColorPalette ncp = bmp.Palette;
            for (int i = 0; i < 256; i++)
                ncp.Entries[i] = Color.FromArgb(255, i, i, i);
            bmp.Palette = ncp;


            readCoeffs();
		}

		double KX1, KX2, KX3, KY1, KY2, KY3;
		private void readCoeffs()
		{
			string fileName = @"C:\Users\" + Environment.UserName + @"\Music\Roombr.txt";

			try
			{
				// Open the stream and read it back.
				using (StreamReader sr = File.OpenText(fileName))
				{
					string s = "";
					while ((s = sr.ReadLine()) != null)
					{
						string[] coefficients = s.Split(',');
						//KX
						KX1 = Convert.ToDouble(coefficients[0].Split('=')[1].ToString());
						KX2 = Convert.ToDouble(coefficients[1].Split('=')[1]);
						KX3 = Convert.ToDouble(coefficients[2].Split('=')[1]);
						//KY
						KY1 = Convert.ToDouble(coefficients[3].Split('=')[1]);
						KY2 = Convert.ToDouble(coefficients[4].Split('=')[1]);
						KY3 = Convert.ToDouble(coefficients[5].Split('=')[1]);
					}
				}
			}
			catch (Exception Ex)
			{

			}
		}

		/*Summary
		   Executes on clicking close button
		*/
		private void Form1_FormClosing(object sender, FormClosingEventArgs e)
		{
			bRunning = false;
			if (tListen != null && tListen.IsAlive == true)
			{
				tListen.Abort();
				tListen.Join();
				tListen = null;
			}


			if (usbDevices != null)
				usbDevices.Dispose();
		}



		/*Summary
		 This is the System event handler.  
		 Enforces valid values for PPX(Packet per transfer)
		*/
		private void PpxBox_SelectedIndexChanged(object sender, EventArgs e)
		{
			if (EndPoint == null) return;

			int ppx = Convert.ToUInt16(32);
			int len = EndPoint.MaxPktSize * ppx;

			int maxLen = 0x400000; // 4MBytes
			if (len > maxLen)
			{
				//ppx = maxLen / (EndPoint.MaxPktSize) / 8 * 8;
				if (EndPoint.MaxPktSize == 0)
				{
					MessageBox.Show("Please correct MaxPacketSize in Descriptor", "Invalid MaxPacketSize");
					return;
				}
				ppx = maxLen / (EndPoint.MaxPktSize);
				ppx -= (ppx % 8);
				MessageBox.Show("Maximum of 4MB per transfer.  Packets reduced.", "Invalid Packets per Xfer.");

				//Update the DropDown list for the packets
				int iIndex = PpxBox.SelectedIndex; // Get the packet index
				PpxBox.Items.Remove(PpxBox.Text); // Remove the Existing  Packet index
				PpxBox.Items.Insert(iIndex, ppx.ToString()); // insert the ppx
				PpxBox.SelectedIndex = iIndex; // update the selected item index

			}


			if ((MyDevice.bSuperSpeed || MyDevice.bHighSpeed) && (EndPoint.Attributes == 1) && (ppx < 8))
			{
				PpxBox.Text = "8";
				MessageBox.Show("Minimum of 8 Packets per Xfer required for HS/SS Isoc.", "Invalid Packets per Xfer.");
			}
			if ((MyDevice.bHighSpeed) && (EndPoint.Attributes == 1))
			{
				if (ppx > 128)
				{
					PpxBox.Text = "128";
					MessageBox.Show("Maximum 128 packets per transfer for High Speed Isoc", "Invalid Packets per Xfer.");
				}
			}

		}

		private void DeviceComboBox_SelectedIndexChanged(object sender, EventArgs e)
		{
			MyDevice = null;
			EndPoint = null;
			SetDevice(true);
		}

		/*Summary
		 This is a system event handler, when the selected index changes(end point selection).
		*/
		private void EndPointsComboBox_SelectedIndexChanged(object sender, EventArgs e)
		{
			// Get the Alt setting
			string sAlt = EndPointsComboBox.Text.Substring(4, 1);
			byte a = Convert.ToByte(sAlt);
			MyDevice.AltIntfc = a;

			// Get the endpoint
			int aX = EndPointsComboBox.Text.LastIndexOf("0x");
			string sAddr = EndPointsComboBox.Text.Substring(aX, 4);
			byte addr = (byte)Util.HexToInt(sAddr);

			EndPoint = MyDevice.EndPointOf(addr);

			// Ensure valid PPX for this endpoint
			PpxBox_SelectedIndexChanged(sender, null);
		}



		/*Summary
		  Executes on Start Button click 
		*/
		private void StartBtn_Click(object sender, System.EventArgs e)
		{
			start();
			vBlank = 29;
		}

		private void start()
		{
			if (MyDevice == null)
				return;

			/*if (QueueBox.Text == "")
			{
				MessageBox.Show("Please Select Xfers to Queue", "Invalid Input");
				return;
			}*/

			if (StartBtn.Text.Equals("Start"))
			{
				sendData();

				//DevicesComboBox.Enabled = false;
				EndPointsComboBox.Enabled = false;
				StartBtn.Text = "Stop";
				StartBtn.BackColor = Color.Gray;
				//PpxBox.Enabled = false;
				//QueueBox.Enabled = false;


				BufSz = bytes;//EndPoint.MaxPktSize * Convert.ToUInt16(256);
				QueueSz = Convert.ToUInt16(txtQueue.Text);
				PPX = Convert.ToUInt16(txtPPX.Text);

				EndPoint.XferSize = BufSz;

				if (EndPoint is CyIsocEndPoint)
					IsoPktBlockSize = (EndPoint as CyIsocEndPoint).GetPktBlockSize(BufSz);
				else
					IsoPktBlockSize = 0;

				bRunning = true;

				tListen = new Thread(new ThreadStart(XferThread));
				tListen.IsBackground = true;
				tListen.Priority = ThreadPriority.Highest;
				tListen.Start();
                robot = new java.awt.Robot();
            }
			else
			{
				if (tListen.IsAlive)
				{
					//DevicesComboBox.Enabled = true;
					EndPointsComboBox.Enabled = true;
					//PpxBox.Enabled = true;
					//QueueBox.Enabled = true;
					StartBtn.Text = "Start";
					bRunning = false;

					/*t2 = DateTime.Now;
					elapsed = t2 - t1;
					xferRate = (long)(XferBytes / elapsed.TotalMilliseconds);
					xferRate = xferRate / (int)100 * (int)100;*/

					if (tListen.Join(5000) == false)
						tListen.Abort();

					
					tListen = null;

					StartBtn.BackColor = Color.Black;
				}

			}
		}


		/*Summary
		  Data Xfer Thread entry point. Starts the thread on Start Button click 
		*/
		public unsafe void XferThread()
		{
			// Setup the queue buffers
			byte[][] cmdBufs = new byte[QueueSz][];
			byte[][] xferBufs = new byte[QueueSz][];
			byte[][] ovLaps = new byte[QueueSz][];
			ISO_PKT_INFO[][] pktsInfo = new ISO_PKT_INFO[QueueSz][];

			//int xStart = 0;

			//////////////////////////////////////////////////////////////////////////////
			///////////////Pin the data buffer memory, so GC won't touch the memory///////
			//////////////////////////////////////////////////////////////////////////////

			GCHandle cmdBufferHandle = GCHandle.Alloc(cmdBufs[0], GCHandleType.Pinned);
			GCHandle xFerBufferHandle = GCHandle.Alloc(xferBufs[0], GCHandleType.Pinned);
			GCHandle overlapDataHandle = GCHandle.Alloc(ovLaps[0], GCHandleType.Pinned);
			GCHandle pktsInfoHandle = GCHandle.Alloc(pktsInfo[0], GCHandleType.Pinned);

			try
			{
				LockNLoad(cmdBufs, xferBufs, ovLaps, pktsInfo);
			}
			catch (NullReferenceException e)
			{
				// This exception gets thrown if the device is unplugged 
				// while we're streaming data
				e.GetBaseException();
				this.Invoke(handleException);
			}

			//////////////////////////////////////////////////////////////////////////////
			///////////////Release the pinned memory and make it available to GC./////////
			//////////////////////////////////////////////////////////////////////////////
			cmdBufferHandle.Free();
			xFerBufferHandle.Free();
			overlapDataHandle.Free();
			pktsInfoHandle.Free();
		}




		/*Summary
		  This is a recursive routine for pinning all the buffers used in the transfer in memory.
		It will get recursively called QueueSz times.  On the QueueSz_th call, it will call
		XferData, which will loop, transferring data, until the stop button is clicked.
		Then, the recursion will unwind.
		*/
		public unsafe void LockNLoad(byte[][] cBufs, byte[][] xBufs, byte[][] oLaps, ISO_PKT_INFO[][] pktsInfo)
		{
			int j = 0;
			int nLocalCount = j;

			GCHandle[] bufSingleTransfer = new GCHandle[QueueSz];
			GCHandle[] bufDataAllocation = new GCHandle[QueueSz];
			GCHandle[] bufPktsInfo = new GCHandle[QueueSz];
			GCHandle[] handleOverlap = new GCHandle[QueueSz];

			while (j < QueueSz)
			{
				// Allocate one set of buffers for the queue, Buffered IO method require user to allocate a buffer as a part of command buffer,
				// the BeginDataXfer does not allocated it. BeginDataXfer will copy the data from the main buffer to the allocated while initializing the commands.
				cBufs[j] = new byte[CyConst.SINGLE_XFER_LEN + IsoPktBlockSize + ((EndPoint.XferMode == XMODE.BUFFERED) ? BufSz : 0)];

				xBufs[j] = new byte[BufSz];

				//initialize the buffer with initial value 0xA5
				for (int iIndex = 0; iIndex < BufSz; iIndex++)
					xBufs[j][iIndex] = DefaultBufInitValue;

				int sz = Math.Max(CyConst.OverlapSignalAllocSize, sizeof(OVERLAPPED));
				oLaps[j] = new byte[sz];
				pktsInfo[j] = new ISO_PKT_INFO[PPX];

				/*/////////////////////////////////////////////////////////////////////////////
				 * 
				 * fixed keyword is getting thrown own by the compiler because the temporary variables 
				 * tL0, tc0 and tb0 aren't used. And for jagged C# array there is no way, we can use this 
				 * temporary variable.
				 * 
				 * Solution  for Variable Pinning:
				 * Its expected that application pin memory before passing the variable address to the
				 * library and subsequently to the windows driver.
				 * 
				 * Cypress Windows Driver is using this very same memory location for data reception or
				 * data delivery to the device.
				 * And, hence .Net Garbage collector isn't expected to move the memory location. And,
				 * Pinning the memory location is essential. And, not through FIXED keyword, because of 
				 * non-usability of temporary variable.
				 * 
				/////////////////////////////////////////////////////////////////////////////*/
				//fixed (byte* tL0 = oLaps[j], tc0 = cBufs[j], tb0 = xBufs[j])  // Pin the buffers in memory
				//////////////////////////////////////////////////////////////////////////////////////////////
				bufSingleTransfer[j] = GCHandle.Alloc(cBufs[j], GCHandleType.Pinned);
				bufDataAllocation[j] = GCHandle.Alloc(xBufs[j], GCHandleType.Pinned);
				bufPktsInfo[j] = GCHandle.Alloc(pktsInfo[j], GCHandleType.Pinned);
				handleOverlap[j] = GCHandle.Alloc(oLaps[j], GCHandleType.Pinned);
				// oLaps "fixed" keyword variable is in use. So, we are good.
				/////////////////////////////////////////////////////////////////////////////////////////////            

				unsafe
				{
					fixed (byte* tL0 = oLaps[j])
					{
						CyUSB.OVERLAPPED ovLapStatus = new CyUSB.OVERLAPPED();
						ovLapStatus = (CyUSB.OVERLAPPED)Marshal.PtrToStructure(handleOverlap[j].AddrOfPinnedObject(), typeof(CyUSB.OVERLAPPED));
						ovLapStatus.hEvent = (IntPtr)PInvoke.CreateEvent(0, 0, 0, 0);
						Marshal.StructureToPtr(ovLapStatus, handleOverlap[j].AddrOfPinnedObject(), true);

						// Pre-load the queue with a request
						int len = BufSz;
						if (EndPoint.BeginDataXfer(ref cBufs[j], ref xBufs[j], ref len, ref oLaps[j]) == false)
							Failures++;
					}
					j++;
				}
			}

			XferData(cBufs, xBufs, oLaps, pktsInfo, handleOverlap);          // All loaded. Let's go!

			unsafe
			{
				for (nLocalCount = 0; nLocalCount < QueueSz; nLocalCount++)
				{
					CyUSB.OVERLAPPED ovLapStatus = new CyUSB.OVERLAPPED();
					ovLapStatus = (CyUSB.OVERLAPPED)Marshal.PtrToStructure(handleOverlap[nLocalCount].AddrOfPinnedObject(), typeof(CyUSB.OVERLAPPED));
					PInvoke.CloseHandle(ovLapStatus.hEvent);

					/*////////////////////////////////////////////////////////////////////////////////////////////
					 * 
					 * Release the pinned allocation handles.
					 * 
					////////////////////////////////////////////////////////////////////////////////////////////*/
					bufSingleTransfer[nLocalCount].Free();
					bufDataAllocation[nLocalCount].Free();
					bufPktsInfo[nLocalCount].Free();
					handleOverlap[nLocalCount].Free();

					cBufs[nLocalCount] = null;
					xBufs[nLocalCount] = null;
					oLaps[nLocalCount] = null;
				}
			}
			GC.Collect();
		}

		int fps = 0,frames = 0;

		/*Summary
		  Called at the end of recursive method, LockNLoad().
		  XferData() implements the infinite transfer loop
		*/
		public unsafe void XferData(byte[][] cBufs, byte[][] xBufs, byte[][] oLaps, ISO_PKT_INFO[][] pktsInfo, GCHandle[] handleOverlap)
		{
			int k = 0;
			int len = 0;

			Successes = 0;
			Failures = 0;

			XferBytes = 0;
			t1 = DateTime.Now;
			long nIteration = 0;
			CyUSB.OVERLAPPED ovData = new CyUSB.OVERLAPPED();

			for (; bRunning;)
			{
				nIteration++;
				// WaitForXfer
				unsafe
				{
					//fixed (byte* tmpOvlap = oLaps[k])
					{
						ovData = (CyUSB.OVERLAPPED)Marshal.PtrToStructure(handleOverlap[k].AddrOfPinnedObject(), typeof(CyUSB.OVERLAPPED));
						if (!EndPoint.WaitForXfer(ovData.hEvent, 1000))
						{
							EndPoint.Abort();
							PInvoke.WaitForSingleObject(ovData.hEvent, 500);
						}
					}
				}

				if (EndPoint.Attributes == 1)
				{
					CyIsocEndPoint isoc = EndPoint as CyIsocEndPoint;
					// FinishDataXfer
					if (isoc.FinishDataXfer(ref cBufs[k], ref xBufs[k], ref len, ref oLaps[k], ref pktsInfo[k]))
					{
						//XferBytes += len;
						//Successes++;

						ISO_PKT_INFO[] pkts = pktsInfo[k];

						for (int j = 0; j < PPX; j++)
						{
							if (pkts[j].Status == 0)
							{
								XferBytes += pkts[j].Length;

								Successes++;
							}
							else
								Failures++;

							pkts[j].Length = 0;
						}

					}
					else
						Failures++;
				}
				else
				{
					// FinishDataXfer
					if (EndPoint.FinishDataXfer(ref cBufs[k], ref xBufs[k], ref len, ref oLaps[k]))
					{
						XferBytes += len;
						Successes++;
						frames++;
					}
					else
						Failures++;
				}

				// Re-submit this buffer into the queue
				len = BufSz;
				if (EndPoint.BeginDataXfer(ref cBufs[k], ref xBufs[k], ref len, ref oLaps[k]) == false)
					Failures++;


				sample = xBufs[k];



				// do something with your image, e.g. save it to disc


				//Invoke(new MethodInvoker(showDataAsImage));
				//showDataAsImage(sample);
				//Thread.Sleep(100);

				k++;
				if (k == QueueSz)  // Only update displayed stats once each time through the queue
				{
					k = 0;

					t2 = DateTime.Now;
					elapsed = t2 - t1;

					fps = frames;//(int) (frames / elapsed.TotalSeconds);
					frames = 0;
					t1 = t2;

                    /*xferRate = (long)(XferBytes / elapsed.TotalMilliseconds);
					xferRate = xferRate / (int)100 * (int)100;*/


                    // Call StatusUpdate() in the main thread
                    if (bRunning == true) this.Invoke(updateUI);

					// For small QueueSz or PPX, the loop is too tight for UI thread to ever get service.   
					// Without this, app hangs in those scenarios.
					Thread.Sleep(0);
				}
				Thread.Sleep(0);

			} // End infinite loop
			// Let's recall all the queued buffer and abort the end point.
			EndPoint.Abort();
		}

		/*Summary
		  The callback routine delegated to updateUI.
		*/
		public void StatusUpdate()
		{

			showDataAsImage();
		}


		/*Summary
		  The callback routine delegated to handleException.
		*/
		public void ThreadException()
		{
			StartBtn.Text = "Start";
			bRunning = false;

			/*t2 = DateTime.Now;
			elapsed = t2 - t1;
			xferRate = (long)(XferBytes / elapsed.TotalMilliseconds);
			xferRate = xferRate / (int)100 * (int)100;*/

			tListen = null;

			StartBtn.BackColor = Color.Aquamarine;

		}

		private void PerfTimer_Tick(object sender, EventArgs e)
		{

		}

		private void sendData()
		{
			/*usbDevices = new USBDeviceList(CyConst.DEVICES_CYUSB);
			myDevice = usbDevices[0x04B4, 0x1003] as CyUSBDevice;*/

			if (MyDevice != null)     //Enter this loop only if device is attached
			{
				//Assigning the object
				CtrlEndPt = MyDevice.ControlEndPt;
				int len = 0;
				byte[] bufBegin = new byte[] { 0x00, 0x00, 0x00 };

				//Vendor Command Format : 0xAA to configure the Image Sensor and add the Header
				CtrlEndPt.Target = CyConst.TGT_DEVICE;
				CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
				CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
				CtrlEndPt.ReqCode = VX_AA;                               // to configure Image sensor
				CtrlEndPt.Value = 0;
				CtrlEndPt.Index = 0;
				CtrlEndPt.XferData(ref bufBegin, ref len);              //asking for the 512 bytes buffer containing the 5 bytes Header
																		// Thread.Sleep(20);
																		// Open the file for reading
				using (StreamReader sr = new StreamReader(configFile))
				{
					string line;

					// Read and display lines until the end of the file
					while ((line = sr.ReadLine()) != null)
					{
						string[] splitted = line.Split(',');
						string first = splitted[0].Trim();
						string Second = splitted[1].Trim();

						MatchCollection matches = Regex.Matches(first, @"\b0x[0-9A-Fa-f]+\b");

						string numericPhone = new String(Second.TakeWhile(Char.IsDigit).ToArray());
						//string numericPhone = new string(Second.Take(6).ToArray());
						int decimalValue = Convert.ToInt32(numericPhone, 10);
						// Process each line here
						// Find decimal numbers in the line using regular expressions
						//MatchCollection matches = Regex.Matches(line, @"\b\d+\b");

						// Convert decimal numbers to hexadecimal
						foreach (Match match in matches)
						{
							string cap = match.Value.Substring(2);
							int dec = Convert.ToInt32(cap, 16);
							//string hexValue = decimalValue.ToString("X");
							bufBegin[0] = Convert.ToByte(dec & 0x00FF);
							bufBegin[1] = Convert.ToByte((decimalValue & 0xFF00) >> 8);
							bufBegin[2] = Convert.ToByte(decimalValue & 0x00FF);
							len = 0X03;
							//Vendor Command Format : 0xFE to configure the Image Sensor and add the Header
							CtrlEndPt.Target = CyConst.TGT_DEVICE;
							CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
							CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
							CtrlEndPt.ReqCode = VX_FE;                               // to configure Image sensor
							CtrlEndPt.Value = 0;
							CtrlEndPt.Index = 0;
							CtrlEndPt.XferData(ref bufBegin, ref len);
							//statusBar1.Text = bufBegin.ToString();
						}
						// Console.WriteLine(line);
					}
					//Vendor Command Format : 0xF5 to start stream from the Image Sensor and add the Header
					len = 1;
					bufBegin[0] = 0x01;
					CtrlEndPt.Target = CyConst.TGT_DEVICE;
					CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
					CtrlEndPt.Direction = CyConst.DIR_FROM_DEVICE;
					CtrlEndPt.ReqCode = VX_F5;                               // to configure Image sensor
					CtrlEndPt.Value = 0;
					CtrlEndPt.Index = 0;
					CtrlEndPt.XferData(ref bufBegin, ref len);

					Thread.Sleep(0);
				}
			}
		}


		int x, y, prevX, prevY, longPress = 0;
		Stopwatch watch;
		long elapsedMs = 0;

		int count = 0;
        private void button3_Click(object sender, EventArgs e)
        {
			setVBlanking(33);
			pixelClkControl();
			//Reset();
        }

		int vBlank = 33;
		private void setVBlanking(int vB)
		{
            //int vBlank = Convert.ToInt32(txtVBlank.Text);
            byte[] bufBegin = { 0x00, 0x00, 0x00 };

            int dec = Convert.ToInt32(5);
            bufBegin[0] = Convert.ToByte(0x05 & 0x00FF);
            bufBegin[1] = Convert.ToByte((vB & 0xFF00) >> 8);
            bufBegin[2] = Convert.ToByte(vB & 0x00FF);
            int len = 0X03;
            //Vendor Command Format : 0xFE to configure the Image Sensor and add the Header
            CtrlEndPt.Target = CyConst.TGT_DEVICE;
            CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
            CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
            CtrlEndPt.ReqCode = VX_FE;                               // to configure Image sensor
            CtrlEndPt.Value = 0;
            CtrlEndPt.Index = 0;
            CtrlEndPt.XferData(ref bufBegin, ref len);
			vBlank += 10;
			if (vBlank > 820)
			{
				vBlank = 31;
			}
        }

		int pxlClk = 0;
        private void pixelClkControl()
        {
            int vBlank = Convert.ToInt32(txtVBlank.Text);
            byte[] bufBegin = { 0x00, 0x00, 0x00 };

            int dec = Convert.ToInt32(72);
            bufBegin[0] = Convert.ToByte(dec & 0x00FF);
            bufBegin[1] = Convert.ToByte((vBlank & 0xFF00) >> 8);
            bufBegin[2] = Convert.ToByte(vBlank & 0x00FF);
            int len = 0X03;
            //Vendor Command Format : 0xFE to configure the Image Sensor and add the Header
            CtrlEndPt.Target = CyConst.TGT_DEVICE;
            CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
            CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
            CtrlEndPt.ReqCode = VX_FE;                               // to configure Image sensor
            CtrlEndPt.Value = 0;
            CtrlEndPt.Index = 0;
            CtrlEndPt.XferData(ref bufBegin, ref len);
			if(pxlClk == 0)
			{
				pxlClk = 1;
			}
			else
			{
				pxlClk = 0;
			}
        }

		int reset = 0;
        private void Reset()
        {
            byte[] bufBegin = { 0x00, 0x00, 0x00 };

            //int dec = Convert.ToInt32(0C);
            bufBegin[0] = Convert.ToByte(0x0C & 0x00FF);
            bufBegin[1] = Convert.ToByte((reset & 0xFF00) >> 8);
            bufBegin[2] = Convert.ToByte(reset & 0x00FF);
            int len = 0X03;
            //Vendor Command Format : 0xFE to configure the Image Sensor and add the Header
            CtrlEndPt.Target = CyConst.TGT_DEVICE;
            CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
            CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
            CtrlEndPt.ReqCode = VX_FE;                               // to configure Image sensor
            CtrlEndPt.Value = 0;
            CtrlEndPt.Index = 0;
            CtrlEndPt.XferData(ref bufBegin, ref len);
			if (reset == 0)
			{
				reset = 1;
			}
			else
			{
				reset = 0;
			}
        }

        System.Windows.Forms.Timer t3;

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
			if(e.KeyCode == Keys.S)
			{
				start();
			}
			if(e.KeyCode == Keys.L)
			{
				lensSwitch();
			}
        }

        Robot robot;
		bool rightClick = false, drag = false;
		private void showDataAsImage()
		{
			try
			{
				//SensorView.Image = null;

				fps = (int)(fps / elapsed.TotalSeconds);
				if(fps<60)
				{
					setVBlanking(33);
					//return;
					//pixelClkControl();
				}
				
				label4.Text = elapsed.TotalSeconds.ToString() + " seconds";

				label7.Text = fps.ToString() + " frames";
				// create a bitmapdata and lock all pixels to be written 
				bmpData = bmp.LockBits(
									new Rectangle(0, 0, Wd, Ht),
									ImageLockMode.WriteOnly, bmp.PixelFormat);

				// copy the data from the byte array into bitmapdata.scan0
				Marshal.Copy(sample, 0, bmpData.Scan0, sample.Length);

				// unlock the pixels
				bmp.UnlockBits(bmpData);
				SensorView.Image = bmp;

				//bmp.Save("C:\\Users\\" + Environment.UserName + "\\Videos\\img.jpg", ImageFormat.Jpeg);


				string co_ordinates = string.Empty;
				int xAxis = 0, yAxis = 0;

				var t = emguContour(bmp);

				if (t.Item1 > 0)
				{
					setVBlanking(Convert.ToInt32(txtVBlank.Text));
					pixelClkControl();
                    xAxis = t.Item1;
					yAxis = t.Item2;
					//MessageBox.Show(xAxis + "," + yAxis);

					x = Convert.ToInt32(KX1 * xAxis + KX2 * yAxis + KX3 + 0.5);
					y = Convert.ToInt32(KY1 * xAxis + KY2 * yAxis + KY3 + 0.5);

					if (watch != null)
					{
						elapsedMs = watch.ElapsedMilliseconds;
					}


					//robot.mouseMove(x, y);

					int diffX = x - prevX;
					int diffY = y - prevY;
					if (elapsedMs > 0 && elapsedMs < 300)
					{
						if(elapsedMs>285)
						{
							//robot.mouseRelease(InputEvent.BUTTON1_MASK);
                            //goto valid;
                        }
						if (diffX > -2 && diffX < 2 && diffY > -2 && diffY < 2)
						{
							longPress++;
							if (longPress >= 7)
							{
								robot.mouseMove(x, y);
								robot.mousePress(InputEvent.BUTTON3_MASK);
								robot.mouseRelease(InputEvent.BUTTON3_MASK);
								robot.delay(1000);
								longPress = 0;
								rightClick = true;
							}
							goto valid;
						}
						else if(elapsedMs<80)
						{

						}
						else if ((diffX >= 0 && diffX < 15) && !rightClick)
						{
							if (elapsedMs < 150 && diffX > 0 && diffX < 10)
							{
								robot.mouseMove(x, y);
								robot.mousePress(InputEvent.BUTTON1_MASK);
								robot.delay(20);
								//robot.mouseRelease(InputEvent.BUTTON1_MASK);
								longPress = 0;
								goto valid;
							}
							else 
							{
								//robot.mouseRelease(InputEvent.BUTTON1_MASK);
                                robot.mousePress(InputEvent.BUTTON1_MASK);
                                robot.mouseMove(x, y);
								robot.delay(10);
                                robot.mouseRelease(InputEvent.BUTTON1_MASK);
                                longPress = 0;
								goto valid;
							}
							
						}

						goto invalid;
					}
					else if (elapsedMs >= 300 && !rightClick)
					{
                        robot.mouseMove(x, y);
						robot.mousePress(InputEvent.BUTTON1_MASK);
						robot.delay(50);
						robot.mouseRelease(InputEvent.BUTTON1_MASK);
						longPress = 0;
					}
					else if(rightClick)
					{
						rightClick = false;
					}
					
					watch = Stopwatch.StartNew();
				}

                valid:
				prevX = x;
				prevY = y;

				invalid:
				string str = "do nothing";
				//do nothing


			}
			catch (Exception ex)
			{
				//MessageBox.Show(ex.ToString() + "\r\n" + "Inner:" + ex.InnerException);
			}
		}

		private Tuple<int,int> emguContour(Bitmap bitmap)
		{
            var image = bitmap.ToImage<Bgr, byte>();
			var gray = image.Convert<Gray, byte>().ThresholdBinary(new Gray(230), new Gray(255));
			VectorOfVectorOfPoint contours = new VectorOfVectorOfPoint();
			Emgu.CV.Mat m = new Emgu.CV.Mat();
            CvInvoke.FindContours(gray, contours, m, Emgu.CV.CvEnum.RetrType.External, Emgu.CV.CvEnum.ChainApproxMethod.ChainApproxSimple);

            for (int i = contours.Size-1; i >= 0; i--)
			{
                //int radius = contours[i].Size;

                double perimeter = CvInvoke.ArcLength(contours[i], true);
				VectorOfPoint approx = new VectorOfPoint();
				CvInvoke.ApproxPolyDP(contours[i], approx, 0.15 * perimeter, true);
                CvInvoke.DrawContours(image, contours, i, new MCvScalar(0, 0, 255), 2);


                //image.Save("C:\\Users\\" + Environment.UserName + "\\Videos\\img_1.jpg");

                //moments  center of the shape
                if (approx.Size >= 2)
				{
                    var moments = CvInvoke.Moments(contours[i]);
                    int x = (int)(moments.M10 / moments.M00);
                    int y = (int)(moments.M01 / moments.M00);
                    return Tuple.Create(x, y);
				}
			}
            return Tuple.Create(0, 0);
        }

		private bool identify()
		{
			using (var src = new Mat("C:\\Users\\" + Environment.UserName + "\\Videos\\img.jpg"))
			using (var gray = new Mat())
			{
				using (var bw = src.CvtColor(ColorConversionCodes.BGR2GRAY)) // convert to grayscale
				{
					// invert b&w (specific to your white on black image)
					Cv2.BitwiseNot(bw, gray);
				}
                Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(Wd, Ht / 3), 0);
				
                Mat m = new Mat();
				// find all contours
				var contours = gray.FindContoursAsArray(RetrievalModes.List, ContourApproximationModes.ApproxSimple);
				
			   
				

				using (var dst = src.Clone())
				{
					foreach (var contour in contours)
					{
						// filter small contours by their area
						var area = Cv2.ContourArea(contour);
						if (area < 5 * 5) // a rect of 15x15, or whatever you see fit
							continue;

						// also filter the whole image contour (by 1% close to the real area), there may be smarter ways...
						if (Math.Abs((area - (src.Width * src.Height)) / area) < 0.01f)
							continue;

						var hull = Cv2.ConvexHull(contour);
						Cv2.Polylines(dst, new[] { hull }, true, Scalar.Red, 2);

						Cv2.Moments(contour);
						
					}

					//using (new Window("src image", src))
					Bitmap pic = MatToBitmap(dst);
					pic.Save("C:\\Users\\" + Environment.UserName + "\\Videos\\img_cntr.jpg");

				}
				if (contours.Length > 2)
				{
					return true;
				}
			}
			return false;
		}

		private static Bitmap MatToBitmap(Mat mat)
		{
			using (var ms = mat.ToMemoryStream())
			{
				return (Bitmap)Image.FromStream(ms);
			}
		}

		bool lensemode = false;

		private void button1_Click(object sender, EventArgs e)
		{
			lensSwitch();
		}

		private void button2_Click(object sender, EventArgs e)
		{
			this.Close();
			Thread th = new Thread(openNewForm);
			th.SetApartmentState(ApartmentState.STA);
			th.Start();
		}

		private void openNewForm()
		{
			Application.Run(new ManualCaliberation());
		}

		private void lensSwitch()
		{
			byte[] bufBegin2 = new byte[] { 0x01 };
			int len = 1;
			if (lensemode == false)
			{
				CtrlEndPt.Target = CyConst.TGT_DEVICE;
				CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
				CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
				CtrlEndPt.ReqCode = VX_FC;
				CtrlEndPt.Value = 0x0040;
				CtrlEndPt.Index = 0;
				CtrlEndPt.XferData(ref bufBegin2, ref len);
				Thread.Sleep(50);
				bufBegin2[0] = 0x00;
				CtrlEndPt.Target = CyConst.TGT_DEVICE;
				CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
				CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
				CtrlEndPt.ReqCode = VX_FC;
				CtrlEndPt.Value = 0x0040;
				CtrlEndPt.Index = 0;
				CtrlEndPt.XferData(ref bufBegin2, ref len);

				lensemode = true;
			}
			else
			{
				CtrlEndPt.Target = CyConst.TGT_DEVICE;
				CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
				CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
				CtrlEndPt.ReqCode = VX_FC;
				CtrlEndPt.Value = 0x0080;
				CtrlEndPt.Index = 0;
				CtrlEndPt.XferData(ref bufBegin2, ref len);
				Thread.Sleep(50);
				bufBegin2[0] = 0x00;
				CtrlEndPt.Target = CyConst.TGT_DEVICE;
				CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
				CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
				CtrlEndPt.ReqCode = VX_FC;
				CtrlEndPt.Value = 0x0080;
				CtrlEndPt.Index = 0;
				CtrlEndPt.XferData(ref bufBegin2, ref len);

				lensemode = false;
			}
		}

	}
}
