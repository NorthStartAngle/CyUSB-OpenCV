Imports System
Imports System.Drawing
Imports System.ComponentModel
Imports System.Windows.Forms
Imports System.Threading
Imports CyUSB
Imports System.Runtime.InteropServices
Imports System.IO
Imports System.Text.RegularExpressions
Imports System.Linq
Imports System.Drawing.Imaging
Imports java.awt
Imports System.Diagnostics
Imports Label = System.Windows.Forms.Label
Imports Button = System.Windows.Forms.Button
Imports Color = System.Drawing.Color
Imports Rectangle = System.Drawing.Rectangle
Imports OpenCvSharp
Imports Mat = OpenCvSharp.Mat
Imports Image = System.Drawing.Image
Imports Emgu.CV.Structure
Imports Emgu.CV
Imports Emgu.CV.Util
Imports KGySoft.CoreLibraries
Imports Emgu.CV.CvEnum

Namespace Streamer

    Public Class Form1
        Inherits Form
        Private bVista As Boolean

        Private usbDevices As USBDeviceList
        Private MyDevice As CyUSBDevice
        Private EndPoint As CyUSBEndPoint

        Private sample As Byte() = New Byte(360959) {}
        Private bmp As Bitmap

        Private CtrlEndPt As CyControlEndPoint = Nothing
        Private configFile As String = Application.StartupPath & "\config\sensor.conf"

        'Vendor Commands
        Const VX_AA As Byte = &HAA                'Init Vendor Command
        Const VX_FE As Byte = &HFE                'Reg Vendor Command
        Const VX_F5 As Byte = &HF5                'Start streaming Vendor Command
        Const VX_FC As Byte = &HFC                'Lense switch between dark and Light Vendor Command
        Const VX_F3 As Byte = &HF3                'Get 32 byte from device Vendor Command
        Const VX_F4 As Byte = &HF4                'Set 32 byte to device Vendor Command

        Private gbl_buffer As Byte()                      'Frame Buffer = Ht*Wd
        Private Shared Ht As Integer = 480                    'int Ht = 480;
        Private Shared Wd As Integer = 752                    'int Wd = 752;
        Private Shared bytes As Integer = Wd * Ht

        'Header        
        Private buffer As Byte() = New Byte(bytes - 1) {}
        Private intialByte As Integer = 512                   '512 bytes; the header 5 bytes are part of a bigger 512 bytes
        Private IsPkt As Boolean = False

        Private t1, t2 As Date
        Private elapsed As TimeSpan
        Private XferBytes As Double
        Private xferRate As Long
        Private Shared DefaultBufInitValue As Byte = &HA5 'VX_F5;//0xA5;


        Private BufSz As Integer
        Private QueueSz As Integer
        Private PPX As Integer
        Private IsoPktBlockSize As Integer
        Private Successes As Integer
        Private Failures As Integer

        Private tListen As Thread
        Private Shared bRunning As Boolean

        ' These are  needed for Thread to update the UI
        Friend Delegate Sub UpdateUICallback()
        Private updateUI As UpdateUICallback
        Private label6 As Label
        Private DevicesComboBox As ComboBox
        Private SensorView As PictureBox
        Private button1 As Button
        Private button2 As Button

        ' These are needed to close the app from the Thread exception(exception handling)
        Friend Delegate Sub ExceptionCallback()
        Private handleException As ExceptionCallback
        Private label7 As Label
        Private button3 As Button
        Private txtQueue As TextBox
        Private txtPPX As TextBox
        Private txtVBlank As TextBox
        Private bmpData As BitmapData


        Public Sub New()
            bVista = Environment.OSVersion.Version.Major < 6 OrElse Environment.OSVersion.Version.Major = 6 AndAlso Environment.OSVersion.Version.Minor = 0

            ' Required for Windows Form Designer support
            InitializeComponent()

            ' Setup the callback routine for updating the UI
            updateUI = New UpdateUICallback(AddressOf StatusUpdate)

            ' Setup the callback routine for NullReference exception handling
            handleException = New ExceptionCallback(AddressOf ThreadException)

            ' Create the list of USB devices attached to the CyUSB3.sys driver.
            usbDevices = New USBDeviceList(CyConst.DEVICES_CYUSB)

            'Assign event handlers for device attachment and device removal.
            AddHandler usbDevices.DeviceAttached, New EventHandler(AddressOf usbDevices_DeviceAttached)
            AddHandler usbDevices.DeviceRemoved, New EventHandler(AddressOf usbDevices_DeviceRemoved)

            'Set and search the device with VID-PID 04b4-1003 and if found, selects the end point
            SetDevice(False)
        End Sub


        ' Summary
        ' 		   This is the event handler for device removal. This method resets the device count and searches for the device with 
        ' 		   VID-PID 04b4-1003
        ' 		
        Private Sub usbDevices_DeviceRemoved(sender As Object, e As EventArgs)
            bRunning = False

            If tListen IsNot Nothing AndAlso tListen.IsAlive = True Then
                tListen.Abort()
                tListen.Join()
                tListen = Nothing
            End If

            MyDevice = Nothing
            EndPoint = Nothing
            SetDevice(False)

            If StartBtn.Text.Equals("Start") = False Then
                If True Then
                    DevicesComboBox.Enabled = True
                    EndPointsComboBox.Enabled = True
                    PpxBox.Enabled = True
                    QueueBox.Enabled = True
                    StartBtn.Text = "Start"
                    bRunning = False

                    t2 = Date.Now
                    elapsed = t2 - t1
                    xferRate = CLng(XferBytes / elapsed.TotalMilliseconds)
                    xferRate = xferRate / 100 * 100

                    StartBtn.BackColor = Color.Aquamarine
                End If

            End If
        End Sub



        ' Summary
        ' 		   This is the event handler for device attachment. This method  searches for the device with 
        ' 		   VID-PID 04b4-00F1
        ' 		
        Private Sub usbDevices_DeviceAttached(sender As Object, e As EventArgs)
            SetDevice(False)
        End Sub



        ' Summary
        ' 		   Search the device with VID-PID 04b4-00F1 and if found, select the end point
        ' 		
        Private Sub SetDevice(bPreserveSelectedDevice As Boolean)
            Dim nCurSelection = 0
            If DevicesComboBox.Items.Count > 0 Then
                nCurSelection = DevicesComboBox.SelectedIndex
                DevicesComboBox.Items.Clear()
            End If
            Dim nDeviceList = usbDevices.Count
            For nCount = 0 To nDeviceList - 1
                Dim fxDevice = usbDevices(nCount)
                Dim strmsg As String
                strmsg = "(0x" & fxDevice.VendorID.ToString("X4") & " - 0x" & fxDevice.ProductID.ToString("X4") & ") " & fxDevice.FriendlyName
                DevicesComboBox.Items.Add(strmsg)
            Next

            If DevicesComboBox.Items.Count > 0 Then DevicesComboBox.SelectedIndex = If(bPreserveSelectedDevice = True, nCurSelection, 0)

            Dim dev = usbDevices(DevicesComboBox.SelectedIndex)

            If dev IsNot Nothing Then
                MyDevice = CType(dev, CyUSBDevice)

                GetEndpointsOfNode(MyDevice.Tree)
                PpxBox.Text = "" 'Set default value to 8 Packets
                QueueBox.Text = ""
                If EndPointsComboBox.Items.Count > 0 Then
                    EndPointsComboBox.SelectedIndex = 0
                    'start();
                    StartBtn.Enabled = True
                Else
                    StartBtn.Enabled = False
                End If

                Text = MyDevice.FriendlyName
            Else
                StartBtn.Enabled = False
                EndPointsComboBox.Items.Clear()
                EndPointsComboBox.Text = ""
                Text = "C# Streamer - no device"
            End If
        End Sub



        ' Summary
        ' 		   Recursive routine populates EndPointsComboBox with strings 
        ' 		   representing all the endpoints in the device.
        ' 		
        Private Sub GetEndpointsOfNode(devTree As TreeNode)
            'EndPointsComboBox.Items.Clear();
            For Each node As TreeNode In devTree.Nodes
                If node.Nodes.Count > 0 Then
                    GetEndpointsOfNode(node)
                Else
                    Dim ept As CyUSBEndPoint = TryCast(node.Tag, CyUSBEndPoint)
                    'return;
                    If ept Is Nothing Then
                    ElseIf Not node.Text.Contains("Control") Then
                        Dim ifc As CyUSBInterface = TryCast(node.Parent.Tag, CyUSBInterface)
                        Dim s = String.Format("ALT-{0}, {1} Byte {2}", ifc.bAlternateSetting, ept.MaxPktSize, node.Text)
                        EndPointsComboBox.Items.Add(s)
                    End If

                End If
            Next

        End Sub




        ' Summary
        ' 		   Clean up any resources being used.
        ' 		
        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then
                If components IsNot Nothing Then
                    components.Dispose()
                End If
            End If
            MyBase.Dispose(disposing)
        End Sub


#Region "Windows Form Designer generated code"

        Private mainMenu As MainMenu
        Private menuItem1 As Windows.Forms.MenuItem
        Private menuItem2 As Windows.Forms.MenuItem
        Private ExitItem As Windows.Forms.MenuItem
        Private menuItem3 As Windows.Forms.MenuItem
        Private AboutItem As Windows.Forms.MenuItem
        Private label1 As Label
        Private label2 As Label
        Private label3 As Label
        Private label4 As Label
        Private PpxBox As ComboBox
        Private QueueBox As ComboBox
        Private SuccessBox As TextBox
        Private groupBox1 As GroupBox
        Private ProgressBar As ProgressBar
        Private StartBtn As Button
        Private ThroughputLabel As Label
        Private FailuresBox As TextBox
        Private EndPointsComboBox As ComboBox
        Private label5 As Label

        Private components As IContainer

        ''' <summary>
        ''' Required method for Designer support - do not modify
        ''' the contents of this method with the code editor.
        ''' </summary>
        Private Sub InitializeComponent()
            components = New ComponentModel.Container()
            Dim resources As ComponentResourceManager = New ComponentResourceManager(GetType(Form1))
            mainMenu = New MainMenu(components)
            menuItem1 = New Windows.Forms.MenuItem()
            menuItem2 = New Windows.Forms.MenuItem()
            ExitItem = New Windows.Forms.MenuItem()
            menuItem3 = New Windows.Forms.MenuItem()
            AboutItem = New Windows.Forms.MenuItem()
            label1 = New Label()
            label2 = New Label()
            label3 = New Label()
            label4 = New Label()
            PpxBox = New ComboBox()
            QueueBox = New ComboBox()
            SuccessBox = New TextBox()
            FailuresBox = New TextBox()
            groupBox1 = New GroupBox()
            ThroughputLabel = New Label()
            ProgressBar = New ProgressBar()
            StartBtn = New Button()
            EndPointsComboBox = New ComboBox()
            label5 = New Label()
            label6 = New Label()
            DevicesComboBox = New ComboBox()
            SensorView = New PictureBox()
            button1 = New Button()
            button2 = New Button()
            label7 = New Label()
            button3 = New Button()
            txtQueue = New TextBox()
            txtPPX = New TextBox()
            txtVBlank = New TextBox()
            groupBox1.SuspendLayout()
            CType(SensorView, ISupportInitialize).BeginInit()
            SuspendLayout()
            ' 
            ' mainMenu
            ' 
            mainMenu.MenuItems.AddRange(New Windows.Forms.MenuItem() {menuItem1, menuItem3})
            ' 
            ' menuItem1
            ' 
            menuItem1.Index = 0
            menuItem1.MenuItems.AddRange(New Windows.Forms.MenuItem() {menuItem2, ExitItem})
            menuItem1.Text = "File"
            ' 
            ' menuItem2
            ' 
            menuItem2.Index = 0
            menuItem2.Text = "-"
            ' 
            ' ExitItem
            ' 
            ExitItem.Index = 1
            ExitItem.Text = "Exit"
            AddHandler ExitItem.Click, New EventHandler(AddressOf ExitItem_Click)
            ' 
            ' menuItem3
            ' 
            menuItem3.Index = 1
            menuItem3.MenuItems.AddRange(New Windows.Forms.MenuItem() {AboutItem})
            menuItem3.Text = "Help"
            menuItem3.Visible = False
            ' 
            ' AboutItem
            ' 
            AboutItem.Index = 0
            AboutItem.Text = "About"
            AddHandler AboutItem.Click, New EventHandler(AddressOf AboutItem_Click)
            ' 
            ' label1
            ' 
            label1.Location = New Drawing.Point(17, 102)
            label1.Name = "label1"
            label1.Size = New Drawing.Size(89, 16)
            label1.TabIndex = 0
            label1.Text = "Packets per Xfer"
            label1.TextAlign = ContentAlignment.BottomLeft
            ' 
            ' label2
            ' 
            label2.Location = New Drawing.Point(17, 141)
            label2.Name = "label2"
            label2.Size = New Drawing.Size(89, 17)
            label2.TabIndex = 1
            label2.Text = "Xfers to Queue"
            label2.TextAlign = ContentAlignment.BottomLeft
            ' 
            ' label3
            ' 
            label3.Location = New Drawing.Point(216, 102)
            label3.Name = "label3"
            label3.Size = New Drawing.Size(64, 17)
            label3.TabIndex = 2
            label3.Text = "Successes"
            label3.TextAlign = ContentAlignment.BottomLeft
            label3.Visible = False
            ' 
            ' label4
            ' 
            label4.Location = New Drawing.Point(191, 433)
            label4.Name = "label4"
            label4.Size = New Drawing.Size(142, 18)
            label4.TabIndex = 3
            label4.Text = "Failures"
            label4.TextAlign = ContentAlignment.BottomLeft
            ' 
            ' PpxBox
            ' 
            PpxBox.DropDownStyle = ComboBoxStyle.DropDownList
            PpxBox.Items.AddRange(New Object() {"1", "2", "4", "8", "16", "32", "64", "128", "256", "512"})
            PpxBox.Location = New Drawing.Point(115, 102)
            PpxBox.Name = "PpxBox"
            PpxBox.Size = New Drawing.Size(64, 21)
            PpxBox.TabIndex = 1
            AddHandler PpxBox.SelectedIndexChanged, New EventHandler(AddressOf PpxBox_SelectedIndexChanged)
            ' 
            ' QueueBox
            ' 
            QueueBox.DropDownStyle = ComboBoxStyle.DropDownList
            QueueBox.Items.AddRange(New Object() {"1", "2", "4", "8", "16", "32", "64", "128"})
            QueueBox.Location = New Drawing.Point(115, 141)
            QueueBox.Name = "QueueBox"
            QueueBox.Size = New Drawing.Size(64, 21)
            QueueBox.TabIndex = 2
            ' 
            ' SuccessBox
            ' 
            SuccessBox.Location = New Drawing.Point(283, 102)
            SuccessBox.Name = "SuccessBox"
            SuccessBox.Size = New Drawing.Size(72, 20)
            SuccessBox.TabIndex = 6
            SuccessBox.TabStop = False
            SuccessBox.Text = "18"
            SuccessBox.TextAlign = HorizontalAlignment.Right
            SuccessBox.Visible = False
            ' 
            ' FailuresBox
            ' 
            FailuresBox.Location = New Drawing.Point(283, 142)
            FailuresBox.Name = "FailuresBox"
            FailuresBox.Size = New Drawing.Size(72, 20)
            FailuresBox.TabIndex = 7
            FailuresBox.TabStop = False
            FailuresBox.Text = "2"
            FailuresBox.TextAlign = HorizontalAlignment.Right
            FailuresBox.Visible = False
            ' 
            ' groupBox1
            ' 
            groupBox1.Controls.Add(ThroughputLabel)
            groupBox1.Controls.Add(ProgressBar)
            groupBox1.Location = New Drawing.Point(7, 298)
            groupBox1.Name = "groupBox1"
            groupBox1.Size = New Drawing.Size(337, 60)
            groupBox1.TabIndex = 8
            groupBox1.TabStop = False
            groupBox1.Text = " Throughput (KBps) "
            groupBox1.Visible = False
            ' 
            ' ThroughputLabel
            ' 
            ThroughputLabel.Font = New Drawing.Font("Microsoft Sans Serif", 8.25F, FontStyle.Bold, GraphicsUnit.Point, 0)
            ThroughputLabel.Location = New Drawing.Point(114, 38)
            ThroughputLabel.Name = "ThroughputLabel"
            ThroughputLabel.Size = New Drawing.Size(100, 16)
            ThroughputLabel.TabIndex = 1
            ThroughputLabel.Text = "0"
            ThroughputLabel.TextAlign = ContentAlignment.BottomCenter
            ' 
            ' ProgressBar
            ' 
            ProgressBar.ForeColor = SystemColors.HotTrack
            ProgressBar.Location = New Drawing.Point(16, 25)
            ProgressBar.Maximum = 500000
            ProgressBar.Name = "ProgressBar"
            ProgressBar.Size = New Drawing.Size(294, 10)
            ProgressBar.TabIndex = 0
            ' 
            ' StartBtn
            ' 
            StartBtn.BackColor = Color.Black
            StartBtn.FlatStyle = FlatStyle.Popup
            StartBtn.Font = New Drawing.Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, 0)
            StartBtn.ForeColor = SystemColors.ButtonHighlight
            StartBtn.Location = New Drawing.Point(255, 504)
            StartBtn.Name = "StartBtn"
            StartBtn.Size = New Drawing.Size(127, 31)
            StartBtn.TabIndex = 3
            StartBtn.Text = "Start"
            StartBtn.UseVisualStyleBackColor = False
            AddHandler StartBtn.Click, New EventHandler(AddressOf StartBtn_Click)
            ' 
            ' EndPointsComboBox
            ' 
            EndPointsComboBox.DropDownHeight = 120
            EndPointsComboBox.DropDownStyle = ComboBoxStyle.DropDownList
            EndPointsComboBox.FormattingEnabled = True
            EndPointsComboBox.IntegralHeight = False
            EndPointsComboBox.Location = New Drawing.Point(115, 63)
            EndPointsComboBox.Name = "EndPointsComboBox"
            EndPointsComboBox.Size = New Drawing.Size(240, 21)
            EndPointsComboBox.TabIndex = 0
            EndPointsComboBox.Visible = False
            AddHandler EndPointsComboBox.SelectedIndexChanged, New EventHandler(AddressOf EndPointsComboBox_SelectedIndexChanged)
            ' 
            ' label5
            ' 
            label5.AutoSize = True
            label5.Location = New Drawing.Point(17, 66)
            label5.Name = "label5"
            label5.Size = New Drawing.Size(94, 13)
            label5.TabIndex = 11
            label5.Text = "Endpoint . . . . . . . "
            label5.Visible = False
            ' 
            ' label6
            ' 
            label6.AutoSize = True
            label6.Location = New Drawing.Point(17, 26)
            label6.Name = "label6"
            label6.Size = New Drawing.Size(96, 13)
            label6.TabIndex = 13
            label6.Text = "Device Connected"
            label6.Visible = False
            ' 
            ' DevicesComboBox
            ' 
            DevicesComboBox.DropDownStyle = ComboBoxStyle.DropDownList
            DevicesComboBox.FormattingEnabled = True
            DevicesComboBox.Location = New Drawing.Point(115, 22)
            DevicesComboBox.Name = "DevicesComboBox"
            DevicesComboBox.Size = New Drawing.Size(240, 21)
            DevicesComboBox.TabIndex = 14
            DevicesComboBox.Visible = False
            AddHandler DevicesComboBox.SelectionChangeCommitted, New EventHandler(AddressOf DeviceComboBox_SelectedIndexChanged)
            ' 
            ' SensorView
            ' 
            SensorView.Location = New Drawing.Point(395, 4)
            SensorView.Margin = New Padding(6)
            SensorView.Name = "SensorView"
            SensorView.Size = New Drawing.Size(656, 461)
            SensorView.SizeMode = PictureBoxSizeMode.StretchImage
            SensorView.TabIndex = 15
            SensorView.TabStop = False
            ' 
            ' button1
            ' 
            button1.BackColor = Color.Aquamarine
            button1.Location = New Drawing.Point(87, 180)
            button1.Name = "button1"
            button1.Size = New Drawing.Size(128, 31)
            button1.TabIndex = 16
            button1.Text = "Lens Switch"
            button1.UseVisualStyleBackColor = False
            AddHandler button1.Click, New EventHandler(AddressOf button1_Click)
            ' 
            ' button2
            ' 
            button2.BackColor = Color.Black
            button2.FlatStyle = FlatStyle.Popup
            button2.Font = New Drawing.Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, 0)
            button2.ForeColor = SystemColors.ButtonHighlight
            button2.Location = New Drawing.Point(7, 522)
            button2.Name = "button2"
            button2.Size = New Drawing.Size(128, 31)
            button2.TabIndex = 17
            button2.Text = "Caliberate"
            button2.UseVisualStyleBackColor = False
            AddHandler button2.Click, New EventHandler(AddressOf button2_Click)
            ' 
            ' label7
            ' 
            label7.Location = New Drawing.Point(191, 399)
            label7.Name = "label7"
            label7.Size = New Drawing.Size(89, 17)
            label7.TabIndex = 18
            label7.Text = "Xfers to Queue"
            label7.TextAlign = ContentAlignment.BottomLeft
            ' 
            ' button3
            ' 
            button3.BackColor = Color.Black
            button3.FlatStyle = FlatStyle.Popup
            button3.Font = New Drawing.Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, 0)
            button3.ForeColor = SystemColors.ButtonHighlight
            button3.Location = New Drawing.Point(219, 219)
            button3.Name = "button3"
            button3.Size = New Drawing.Size(128, 29)
            button3.TabIndex = 19
            button3.Text = "V Blank"
            button3.UseVisualStyleBackColor = False
            AddHandler button3.Click, New EventHandler(AddressOf button3_Click)
            ' 
            ' txtQueue
            ' 
            txtQueue.Location = New Drawing.Point(202, 143)
            txtQueue.Name = "txtQueue"
            txtQueue.Size = New Drawing.Size(72, 20)
            txtQueue.TabIndex = 21
            txtQueue.TabStop = False
            txtQueue.Text = "2"
            txtQueue.TextAlign = HorizontalAlignment.Right
            ' 
            ' txtPPX
            ' 
            txtPPX.Location = New Drawing.Point(202, 103)
            txtPPX.Name = "txtPPX"
            txtPPX.Size = New Drawing.Size(72, 20)
            txtPPX.TabIndex = 20
            txtPPX.TabStop = False
            txtPPX.Text = "16"
            txtPPX.TextAlign = HorizontalAlignment.Right
            ' 
            ' txtVBlank
            ' 
            txtVBlank.Location = New Drawing.Point(244, 186)
            txtVBlank.Name = "txtVBlank"
            txtVBlank.Size = New Drawing.Size(73, 20)
            txtVBlank.TabIndex = 22
            txtVBlank.TabStop = False
            txtVBlank.Text = "39"
            txtVBlank.TextAlign = HorizontalAlignment.Right
            ' 
            ' Form1
            ' 
            AutoScaleBaseSize = New Drawing.Size(5, 13)
            ClientSize = New Drawing.Size(1921, 803)
            Controls.Add(txtVBlank)
            Controls.Add(txtQueue)
            Controls.Add(txtPPX)
            Controls.Add(button3)
            Controls.Add(label7)
            Controls.Add(button2)
            Controls.Add(button1)
            Controls.Add(StartBtn)
            Controls.Add(SensorView)
            Controls.Add(DevicesComboBox)
            Controls.Add(label6)
            Controls.Add(label5)
            Controls.Add(EndPointsComboBox)
            Controls.Add(groupBox1)
            Controls.Add(FailuresBox)
            Controls.Add(SuccessBox)
            Controls.Add(QueueBox)
            Controls.Add(PpxBox)
            Controls.Add(label4)
            Controls.Add(label3)
            Controls.Add(label2)
            Controls.Add(label1)
            FormBorderStyle = FormBorderStyle.Fixed3D
            Icon = CType(resources.GetObject("$this.Icon"), Icon)
            KeyPreview = True
            Menu = mainMenu
            Name = "Form1"
            StartPosition = FormStartPosition.CenterScreen
            Text = "Roombr Touch"
            AddHandler FormClosing, New FormClosingEventHandler(AddressOf Form1_FormClosing)
            AddHandler Load, New EventHandler(AddressOf Form1_Load)
            AddHandler KeyDown, New KeyEventHandler(AddressOf Form1_KeyDown)
            groupBox1.ResumeLayout(False)
            CType(SensorView, ISupportInitialize).EndInit()
            ResumeLayout(False)
            PerformLayout()

        End Sub
#End Region


        ' Summary
        ' 		   Executes on clicking Help->about
        ' 		
        Private Sub AboutItem_Click(sender As Object, e As EventArgs)
            Dim assemblyList = Util.Assemblies
            MessageBox.Show(assemblyList, Text)
        End Sub



        ' Summary
        ' 		   Executes on clicking File->Exit
        ' 		
        Private Sub ExitItem_Click(sender As Object, e As EventArgs)
            Close()
        End Sub

        Private Sub Form1_Load(sender As Object, e As EventArgs)
            If EndPointsComboBox.Items.Count > 0 Then EndPointsComboBox.SelectedIndex = 0

            bmp = New Bitmap(Wd, Ht, PixelFormat.Format8bppIndexed)

            Dim ncp = bmp.Palette
            For i = 0 To 255
                ncp.Entries(i) = Color.FromArgb(255, i, i, i)
            Next
            bmp.Palette = ncp


            readCoeffs()
        End Sub

        Private KX1, KX2, KX3, KY1, KY2, KY3 As Double
        Private Sub readCoeffs()
            Dim fileName = "C:\Users\" & Environment.UserName & "\Music\Roombr.txt"

            Try
                ' Open the stream and read it back.
                Using sr = File.OpenText(fileName)
                    Dim s = ""
                    While Not Equals((CSharpImpl.__Assign(s, sr.ReadLine())), Nothing)
                        Dim coefficients = s.Split(","c)
                        'KX
                        KX1 = System.Convert.ToDouble(coefficients(0).Split("="c)(1).ToString())
                        KX2 = System.Convert.ToDouble(coefficients(1).Split("="c)(1))
                        KX3 = System.Convert.ToDouble(coefficients(2).Split("="c)(1))
                        'KY
                        KY1 = System.Convert.ToDouble(coefficients(3).Split("="c)(1))
                        KY2 = System.Convert.ToDouble(coefficients(4).Split("="c)(1))
                        KY3 = System.Convert.ToDouble(coefficients(5).Split("="c)(1))
                    End While
                End Using
            Catch Ex As Exception

            End Try
        End Sub

        ' Summary
        ' 		   Executes on clicking close button
        ' 		
        Private Sub Form1_FormClosing(sender As Object, e As FormClosingEventArgs)
            bRunning = False
            If tListen IsNot Nothing AndAlso tListen.IsAlive = True Then
                tListen.Abort()
                tListen.Join()
                tListen = Nothing
            End If


            If usbDevices IsNot Nothing Then usbDevices.Dispose()
        End Sub



        ' Summary
        ' 		 This is the System event handler.  
        ' 		 Enforces valid values for PPX(Packet per transfer)
        ' 		
        Private Sub PpxBox_SelectedIndexChanged(sender As Object, e As EventArgs)
            If EndPoint Is Nothing Then Return

            Dim ppx As Integer = System.Convert.ToUInt16(32)
            Dim len = EndPoint.MaxPktSize * ppx

            Dim maxLen = &H400000 ' 4MBytes
            If len > maxLen Then
                'ppx = maxLen / (EndPoint.MaxPktSize) / 8 * 8;
                If EndPoint.MaxPktSize = 0 Then
                    MessageBox.Show("Please correct MaxPacketSize in Descriptor", "Invalid MaxPacketSize")
                    Return
                End If
                ppx = maxLen / EndPoint.MaxPktSize
                ppx -= ppx Mod 8
                MessageBox.Show("Maximum of 4MB per transfer.  Packets reduced.", "Invalid Packets per Xfer.")

                'Update the DropDown list for the packets
                Dim iIndex = PpxBox.SelectedIndex ' Get the packet index
                PpxBox.Items.Remove(PpxBox.Text) ' Remove the Existing  Packet index
                PpxBox.Items.Insert(iIndex, ppx.ToString()) ' insert the ppx
                PpxBox.SelectedIndex = iIndex ' update the selected item index

            End If


            If (MyDevice.bSuperSpeed OrElse MyDevice.bHighSpeed) AndAlso EndPoint.Attributes = 1 AndAlso ppx < 8 Then
                PpxBox.Text = "8"
                MessageBox.Show("Minimum of 8 Packets per Xfer required for HS/SS Isoc.", "Invalid Packets per Xfer.")
            End If
            If MyDevice.bHighSpeed AndAlso EndPoint.Attributes = 1 Then
                If ppx > 128 Then
                    PpxBox.Text = "128"
                    MessageBox.Show("Maximum 128 packets per transfer for High Speed Isoc", "Invalid Packets per Xfer.")
                End If
            End If

        End Sub

        Private Sub DeviceComboBox_SelectedIndexChanged(sender As Object, e As EventArgs)
            MyDevice = Nothing
            EndPoint = Nothing
            SetDevice(True)
        End Sub

        ' Summary
        ' 		 This is a system event handler, when the selected index changes(end point selection).
        ' 		
        Private Sub EndPointsComboBox_SelectedIndexChanged(sender As Object, e As EventArgs)
            ' Get the Alt setting
            Dim sAlt = EndPointsComboBox.Text.Substring(4, 1)
            Dim a = System.Convert.ToByte(sAlt)
            MyDevice.AltIntfc = a

            ' Get the endpoint
            Dim aX = EndPointsComboBox.Text.LastIndexOf("0x")
            Dim sAddr = EndPointsComboBox.Text.Substring(aX, 4)
            Dim addr As Byte = Util.HexToInt(sAddr)

            EndPoint = MyDevice.EndPointOf(addr)

            ' Ensure valid PPX for this endpoint
            PpxBox_SelectedIndexChanged(sender, Nothing)
        End Sub



        ' Summary
        ' 		  Executes on Start Button click 
        ' 		
        Private Sub StartBtn_Click(sender As Object, e As EventArgs)
            start()
            vBlank = 29
        End Sub

        Private Sub start()
            If MyDevice Is Nothing Then Return

            ' if (QueueBox.Text == "")
            ' 			{
            ' 				MessageBox.Show("Please Select Xfers to Queue", "Invalid Input");
            ' 				return;
            ' 			}

            If StartBtn.Text.Equals("Start") Then
                sendData()

                'DevicesComboBox.Enabled = false;
                EndPointsComboBox.Enabled = False
                StartBtn.Text = "Stop"
                StartBtn.BackColor = Color.Gray
                'PpxBox.Enabled = false;
                'QueueBox.Enabled = false;


                BufSz = bytes 'EndPoint.MaxPktSize * Convert.ToUInt16(256);
                QueueSz = System.Convert.ToUInt16(txtQueue.Text)
                PPX = System.Convert.ToUInt16(txtPPX.Text)

                EndPoint.XferSize = BufSz

                If TypeOf EndPoint Is CyIsocEndPoint Then
                    IsoPktBlockSize = TryCast(EndPoint, CyIsocEndPoint).GetPktBlockSize(BufSz)
                Else
                    IsoPktBlockSize = 0
                End If

                bRunning = True

                tListen = New Thread(New ThreadStart(AddressOf Me.XferThread))
                tListen.IsBackground = True
                tListen.Priority = ThreadPriority.Highest
                tListen.Start()
                robot = New Robot()
            Else
                If tListen.IsAlive Then
                    'DevicesComboBox.Enabled = true;
                    EndPointsComboBox.Enabled = True
                    'PpxBox.Enabled = true;
                    'QueueBox.Enabled = true;
                    StartBtn.Text = "Start"
                    bRunning = False

                    ' t2 = DateTime.Now;
                    ' 					elapsed = t2 - t1;
                    ' 					xferRate = (long)(XferBytes / elapsed.TotalMilliseconds);
                    ' 					xferRate = xferRate / (int)100 * (int)100;

                    If tListen.Join(5000) = False Then tListen.Abort()


                    tListen = Nothing

                    StartBtn.BackColor = Color.Black
                End If

            End If


            ' Summary
            ' 		  Data Xfer Thread entry point. Starts the thread on Start Button click 
            ' 		
            ' Setup the queue buffers

            'int xStart = 0;

            '''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''
            ''''''''''''''Pin the data buffer memory, so GC won't touch the memory///////
            '''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''

            ' This exception gets thrown if the device is unplugged 
            ' while we're streaming data

            '''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''
            ''''''''''''''Release the pinned memory and make it available to GC./////////
            '''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''




            ' Summary
            ' 		  This is a recursive routine for pinning all the buffers used in the transfer in memory.
            ' 		It will get recursively called QueueSz times.  On the QueueSz_th call, it will call
            ' 		XferData, which will loop, transferring data, until the stop button is clicked.
            ' 		Then, the recursion will unwind.
            ' 		
            ' Allocate one set of buffers for the queue, Buffered IO method require user to allocate a buffer as a part of command buffer,
            ' the BeginDataXfer does not allocated it. BeginDataXfer will copy the data from the main buffer to the allocated while initializing the commands.

            'initialize the buffer with initial value 0xA5

            ' /////////////////////////////////////////////////////////////////////////////
            ' 				 * 
            ' 				 * fixed keyword is getting thrown own by the compiler because the temporary variables 
            ' 				 * tL0, tc0 and tb0 aren't used. And for jagged C# array there is no way, we can use this 
            ' 				 * temporary variable.
            ' 				 * 
            ' 				 * Solution  for Variable Pinning:
            ' 				 * Its expected that application pin memory before passing the variable address to the
            ' 				 * library and subsequently to the windows driver.
            ' 				 * 
            ' 				 * Cypress Windows Driver is using this very same memory location for data reception or
            ' 				 * data delivery to the device.
            ' 				 * And, hence .Net Garbage collector isn't expected to move the memory location. And,
            ' 				 * Pinning the memory location is essential. And, not through FIXED keyword, because of 
            ' 				 * non-usability of temporary variable.
            ' 				 * 
            ' 				/////////////////////////////////////////////////////////////////////////////
            'fixed (byte* tL0 = oLaps[j], tc0 = cBufs[j], tb0 = xBufs[j])  // Pin the buffers in memory
            '''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''
            ' oLaps "fixed" keyword variable is in use. So, we are good.
            ''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''            


            ' Pre-load the queue with a request

            ' ////////////////////////////////////////////////////////////////////////////////////////////
            ' 					 * 
            ' 					 * Release the pinned allocation handles.
            ' 					 * 
            ' 					////////////////////////////////////////////////////////////////////////////////////////////
        End Sub          ' All loaded. Let's go!

                ''' Cannot convert MethodDeclarationSyntax, System.NotSupportedException: UnsafeKeyword is not supported!
'''    at ICSharpCode.CodeConverter.VB.SyntaxKindExtensions.ConvertToken(SyntaxKind t, TokenContext context)
'''    at ICSharpCode.CodeConverter.VB.CommonConversions.ConvertModifier(SyntaxToken m, TokenContext context)
'''    at ICSharpCode.CodeConverter.VB.CommonConversions.<>c__DisplayClass33_0.<ConvertModifiersCore>b__3(SyntaxToken x)
'''    at System.Linq.Enumerable.WhereSelectEnumerableIterator`2.MoveNext()
'''    at System.Linq.Enumerable.WhereSelectEnumerableIterator`2.MoveNext()
'''    at System.Collections.Generic.List`1..ctor(IEnumerable`1 collection)
'''    at System.Linq.Enumerable.ToList[TSource](IEnumerable`1 source)
'''    at ICSharpCode.CodeConverter.VB.CommonConversions.ConvertModifiersCore(IReadOnlyCollection`1 modifiers, TokenContext context, Boolean isConstructor)
'''    at ICSharpCode.CodeConverter.VB.NodesVisitor.VisitMethodDeclaration(MethodDeclarationSyntax node)
'''    at Microsoft.CodeAnalysis.CSharp.CSharpSyntaxVisitor`1.Visit(SyntaxNode node)
'''    at ICSharpCode.CodeConverter.VB.CommentConvertingVisitorWrapper`1.Accept(SyntaxNode csNode, Boolean addSourceMapping)
''' 
''' Input:
''' 
''' 
''' 		/*Summary
''' 		  Data Xfer Thread entry point. Starts the thread on Start Button click 
''' 		*/
''' 		public unsafe void XferThread()
''' 		{
''' 			// Setup the queue buffers
''' 			byte[][] cmdBufs = new byte[this.QueueSz][];
''' 			byte[][] xferBufs = new byte[this.QueueSz][];
''' 			byte[][] ovLaps = new byte[this.QueueSz][];
''' 			CyUSB.ISO_PKT_INFO[][] pktsInfo = new CyUSB.ISO_PKT_INFO[this.QueueSz][];
''' 
''' 			//int xStart = 0;
''' 
''' 			//////////////////////////////////////////////////////////////////////////////
''' 			///////////////Pin the data buffer memory, so GC won't touch the memory///////
''' 			//////////////////////////////////////////////////////////////////////////////
''' 
''' 			System.Runtime.InteropServices.GCHandle cmdBufferHandle = System.Runtime.InteropServices.GCHandle.Alloc(cmdBufs[0], System.Runtime.InteropServices.GCHandleType.Pinned);
''' 			System.Runtime.InteropServices.GCHandle xFerBufferHandle = System.Runtime.InteropServices.GCHandle.Alloc(xferBufs[0], System.Runtime.InteropServices.GCHandleType.Pinned);
''' 			System.Runtime.InteropServices.GCHandle overlapDataHandle = System.Runtime.InteropServices.GCHandle.Alloc(ovLaps[0], System.Runtime.InteropServices.GCHandleType.Pinned);
''' 			System.Runtime.InteropServices.GCHandle pktsInfoHandle = System.Runtime.InteropServices.GCHandle.Alloc(pktsInfo[0], System.Runtime.InteropServices.GCHandleType.Pinned);
''' 
''' 			try
''' 			{
''' 				this.LockNLoad(cmdBufs, xferBufs, ovLaps, pktsInfo);
''' 			}
''' 			catch (System.NullReferenceException e)
''' 			{
''' 				// This exception gets thrown if the device is unplugged 
''' 				// while we're streaming data
''' 				e.GetBaseException();
''' 				this.Invoke(this.handleException);
''' 			}
''' 
''' 			//////////////////////////////////////////////////////////////////////////////
''' 			///////////////Release the pinned memory and make it available to GC./////////
''' 			//////////////////////////////////////////////////////////////////////////////
''' 			cmdBufferHandle.Free();
''' 			xFerBufferHandle.Free();
''' 			overlapDataHandle.Free();
''' 			pktsInfoHandle.Free();
''' 		}
''' 
''' 
                ''' Cannot convert MethodDeclarationSyntax, System.NotSupportedException: UnsafeKeyword is not supported!
'''    at ICSharpCode.CodeConverter.VB.SyntaxKindExtensions.ConvertToken(SyntaxKind t, TokenContext context)
'''    at ICSharpCode.CodeConverter.VB.CommonConversions.ConvertModifier(SyntaxToken m, TokenContext context)
'''    at ICSharpCode.CodeConverter.VB.CommonConversions.<>c__DisplayClass33_0.<ConvertModifiersCore>b__3(SyntaxToken x)
'''    at System.Linq.Enumerable.WhereSelectEnumerableIterator`2.MoveNext()
'''    at System.Linq.Enumerable.WhereSelectEnumerableIterator`2.MoveNext()
'''    at System.Collections.Generic.List`1..ctor(IEnumerable`1 collection)
'''    at System.Linq.Enumerable.ToList[TSource](IEnumerable`1 source)
'''    at ICSharpCode.CodeConverter.VB.CommonConversions.ConvertModifiersCore(IReadOnlyCollection`1 modifiers, TokenContext context, Boolean isConstructor)
'''    at ICSharpCode.CodeConverter.VB.NodesVisitor.VisitMethodDeclaration(MethodDeclarationSyntax node)
'''    at Microsoft.CodeAnalysis.CSharp.CSharpSyntaxVisitor`1.Visit(SyntaxNode node)
'''    at ICSharpCode.CodeConverter.VB.CommentConvertingVisitorWrapper`1.Accept(SyntaxNode csNode, Boolean addSourceMapping)
''' 
''' Input:
''' 
''' 
''' 
''' 
''' 		/*Summary
''' 		  This is a recursive routine for pinning all the buffers used in the transfer in memory.
''' 		It will get recursively called QueueSz times.  On the QueueSz_th call, it will call
''' 		XferData, which will loop, transferring data, until the stop button is clicked.
''' 		Then, the recursion will unwind.
''' 		*/
''' 		public unsafe void LockNLoad(byte[][] cBufs, byte[][] xBufs, byte[][] oLaps, CyUSB.ISO_PKT_INFO[][] pktsInfo)
''' 		{
''' 			int j = 0;
''' 			int nLocalCount = j;
''' 
''' 			System.Runtime.InteropServices.GCHandle[] bufSingleTransfer = new System.Runtime.InteropServices.GCHandle[this.QueueSz];
''' 			System.Runtime.InteropServices.GCHandle[] bufDataAllocation = new System.Runtime.InteropServices.GCHandle[this.QueueSz];
''' 			System.Runtime.InteropServices.GCHandle[] bufPktsInfo = new System.Runtime.InteropServices.GCHandle[this.QueueSz];
''' 			System.Runtime.InteropServices.GCHandle[] handleOverlap = new System.Runtime.InteropServices.GCHandle[this.QueueSz];
''' 
''' 			while (j < this.QueueSz)
''' 			{
''' 				// Allocate one set of buffers for the queue, Buffered IO method require user to allocate a buffer as a part of command buffer,
''' 				// the BeginDataXfer does not allocated it. BeginDataXfer will copy the data from the main buffer to the allocated while initializing the commands.
''' 				cBufs[j] = new byte[CyUSB.CyConst.SINGLE_XFER_LEN + this.IsoPktBlockSize + ((this.EndPoint.XferMode == CyUSB.XMODE.BUFFERED) ? this.BufSz : 0)];
''' 
''' 				xBufs[j] = new byte[this.BufSz];
''' 
''' 				//initialize the buffer with initial value 0xA5
''' 				for (int iIndex = 0; iIndex < this.BufSz; iIndex++)
''' 					xBufs[j][iIndex] = Streamer.Form1.DefaultBufInitValue;
''' 
''' 				int sz = System.Math.Max(CyUSB.CyConst.OverlapSignalAllocSize, sizeof(CyUSB.OVERLAPPED));
''' 				oLaps[j] = new byte[sz];
''' 				pktsInfo[j] = new CyUSB.ISO_PKT_INFO[this.PPX];
''' 
''' 				/*/////////////////////////////////////////////////////////////////////////////
''' 				 * 
''' 				 * fixed keyword is getting thrown own by the compiler because the temporary variables 
''' 				 * tL0, tc0 and tb0 aren't used. And for jagged C# array there is no way, we can use this 
''' 				 * temporary variable.
''' 				 * 
''' 				 * Solution  for Variable Pinning:
''' 				 * Its expected that application pin memory before passing the variable address to the
''' 				 * library and subsequently to the windows driver.
''' 				 * 
''' 				 * Cypress Windows Driver is using this very same memory location for data reception or
''' 				 * data delivery to the device.
''' 				 * And, hence .Net Garbage collector isn't expected to move the memory location. And,
''' 				 * Pinning the memory location is essential. And, not through FIXED keyword, because of 
''' 				 * non-usability of temporary variable.
''' 				 * 
''' 				/////////////////////////////////////////////////////////////////////////////*/
''' 				//fixed (byte* tL0 = oLaps[j], tc0 = cBufs[j], tb0 = xBufs[j])  // Pin the buffers in memory
''' 				//////////////////////////////////////////////////////////////////////////////////////////////
''' 				bufSingleTransfer[j] = System.Runtime.InteropServices.GCHandle.Alloc(cBufs[j], System.Runtime.InteropServices.GCHandleType.Pinned);
''' 				bufDataAllocation[j] = System.Runtime.InteropServices.GCHandle.Alloc(xBufs[j], System.Runtime.InteropServices.GCHandleType.Pinned);
''' 				bufPktsInfo[j] = System.Runtime.InteropServices.GCHandle.Alloc(pktsInfo[j], System.Runtime.InteropServices.GCHandleType.Pinned);
''' 				handleOverlap[j] = System.Runtime.InteropServices.GCHandle.Alloc(oLaps[j], System.Runtime.InteropServices.GCHandleType.Pinned);
''' 				// oLaps "fixed" keyword variable is in use. So, we are good.
''' 				/////////////////////////////////////////////////////////////////////////////////////////////            
''' 
''' 				unsafe
''' 				{
''' 					fixed (byte* tL0 = oLaps[j])
''' 					{
''' 						CyUSB.OVERLAPPED ovLapStatus = new CyUSB.OVERLAPPED();
''' 						ovLapStatus = (CyUSB.OVERLAPPED)System.Runtime.InteropServices.Marshal.PtrToStructure(handleOverlap[(System.Int32)(j)].AddrOfPinnedObject(), typeof(CyUSB.OVERLAPPED));
''' 						ovLapStatus.hEvent = (System.IntPtr)CyUSB.PInvoke.CreateEvent(0, 0, 0, 0);
''' 						System.Runtime.InteropServices.Marshal.StructureToPtr(ovLapStatus, handleOverlap[(System.Int32)(j)].AddrOfPinnedObject(), true);
''' 
''' 						// Pre-load the queue with a request
''' 						int len = this.BufSz;
''' 						if (this.EndPoint.BeginDataXfer(ref cBufs[j], ref xBufs[j], ref len, ref oLaps[j]) == false)
''' 							this.Failures++;
''' 					}
''' 					j++;
''' 				}
''' 			}
''' 
''' 			this.XferData(cBufs, xBufs, oLaps, pktsInfo, handleOverlap);          // All loaded. Let's go!
''' 
''' 			unsafe
''' 			{
''' 				for (nLocalCount = 0; nLocalCount < this.QueueSz; nLocalCount++)
''' 				{
''' 					CyUSB.OVERLAPPED ovLapStatus = new CyUSB.OVERLAPPED();
''' 					ovLapStatus = (CyUSB.OVERLAPPED)System.Runtime.InteropServices.Marshal.PtrToStructure(handleOverlap[(System.Int32)(nLocalCount)].AddrOfPinnedObject(), typeof(CyUSB.OVERLAPPED));
''' 					CyUSB.PInvoke.CloseHandle(ovLapStatus.hEvent);
''' 
''' 					/*////////////////////////////////////////////////////////////////////////////////////////////
''' 					 * 
''' 					 * Release the pinned allocation handles.
''' 					 * 
''' 					////////////////////////////////////////////////////////////////////////////////////////////*/
''' 					bufSingleTransfer[(System.Int32)(nLocalCount)].Free();
''' 					bufDataAllocation[(System.Int32)(nLocalCount)].Free();
''' 					bufPktsInfo[(System.Int32)(nLocalCount)].Free();
''' 					handleOverlap[(System.Int32)(nLocalCount)].Free();
''' 
''' 					cBufs[nLocalCount] = null;
''' 					xBufs[nLocalCount] = null;
''' 					oLaps[nLocalCount] = null;
''' 				}
''' 			}
''' 			System.GC.Collect();
''' 		}
''' 
''' 


        ' Summary
        ' 		  Called at the end of recursive method, LockNLoad().
        ' 		  XferData() implements the infinite transfer loop
        ' 		
        ' WaitForXfer
        'fixed (byte* tmpOvlap = oLaps[k])
        ' FinishDataXfer
        'XferBytes += len;
        'Successes++;

        ' FinishDataXfer

        ' Re-submit this buffer into the queue



        ' do something with your image, e.g. save it to disc


        'Invoke(new MethodInvoker(showDataAsImage));
        'showDataAsImage(sample);
        'Thread.Sleep(100);


        ' xferRate = (long)(XferBytes / elapsed.TotalMilliseconds);
        ' 					xferRate = xferRate / (int)100 * (int)100;


        ' Call StatusUpdate() in the main thread

        ' For small QueueSz or PPX, the loop is too tight for UI thread to ever get service.   
        ' Without this, app hangs in those scenarios.
        ' Let's recall all the queued buffer and abort the end point.
        Private fps As Integer = 0, frames As Integer = 0  ' Only update displayed stats once each time through the queue
        '(int) (frames / elapsed.TotalSeconds);
        ' End infinite loop
                ''' Cannot convert MethodDeclarationSyntax, System.NotSupportedException: UnsafeKeyword is not supported!
'''    at ICSharpCode.CodeConverter.VB.SyntaxKindExtensions.ConvertToken(SyntaxKind t, TokenContext context)
'''    at ICSharpCode.CodeConverter.VB.CommonConversions.ConvertModifier(SyntaxToken m, TokenContext context)
'''    at ICSharpCode.CodeConverter.VB.CommonConversions.<>c__DisplayClass33_0.<ConvertModifiersCore>b__3(SyntaxToken x)
'''    at System.Linq.Enumerable.WhereSelectEnumerableIterator`2.MoveNext()
'''    at System.Linq.Enumerable.WhereSelectEnumerableIterator`2.MoveNext()
'''    at System.Collections.Generic.List`1..ctor(IEnumerable`1 collection)
'''    at System.Linq.Enumerable.ToList[TSource](IEnumerable`1 source)
'''    at ICSharpCode.CodeConverter.VB.CommonConversions.ConvertModifiersCore(IReadOnlyCollection`1 modifiers, TokenContext context, Boolean isConstructor)
'''    at ICSharpCode.CodeConverter.VB.NodesVisitor.VisitMethodDeclaration(MethodDeclarationSyntax node)
'''    at Microsoft.CodeAnalysis.CSharp.CSharpSyntaxVisitor`1.Visit(SyntaxNode node)
'''    at ICSharpCode.CodeConverter.VB.CommentConvertingVisitorWrapper`1.Accept(SyntaxNode csNode, Boolean addSourceMapping)
''' 
''' Input:
''' 
''' 		/*Summary
''' 		  Called at the end of recursive method, LockNLoad().
''' 		  XferData() implements the infinite transfer loop
''' 		*/
''' 		public unsafe void XferData(byte[][] cBufs, byte[][] xBufs, byte[][] oLaps, CyUSB.ISO_PKT_INFO[][] pktsInfo, System.Runtime.InteropServices.GCHandle[] handleOverlap)
''' 		{
''' 			int k = 0;
''' 			int len = 0;
''' 
''' 			this.Successes = 0;
''' 			this.Failures = 0;
''' 
''' 			this.XferBytes = 0;
''' 			this.t1 = System.DateTime.Now;
''' 			long nIteration = 0;
''' 			CyUSB.OVERLAPPED ovData = new CyUSB.OVERLAPPED();
''' 
''' 			for (; Streamer.Form1.bRunning;)
''' 			{
''' 				nIteration++;
''' 				// WaitForXfer
''' 				unsafe
''' 				{
''' 					//fixed (byte* tmpOvlap = oLaps[k])
''' 					{
''' 						ovData = (CyUSB.OVERLAPPED)System.Runtime.InteropServices.Marshal.PtrToStructure(handleOverlap[(System.Int32)(k)].AddrOfPinnedObject(), typeof(CyUSB.OVERLAPPED));
''' 						if (!this.EndPoint.WaitForXfer(ovData.hEvent, 1000))
''' 						{
''' 							this.EndPoint.Abort();
''' 							CyUSB.PInvoke.WaitForSingleObject(ovData.hEvent, 500);
''' 						}
''' 					}
''' 				}
''' 
''' 				if (this.EndPoint.Attributes == 1)
''' 				{
''' 					CyUSB.CyIsocEndPoint isoc = this.EndPoint as CyUSB.CyIsocEndPoint;
''' 					// FinishDataXfer
''' 					if (isoc.FinishDataXfer(ref cBufs[k], ref xBufs[k], ref len, ref oLaps[k], ref pktsInfo[k]))
''' 					{
''' 						//XferBytes += len;
''' 						//Successes++;
''' 
''' 						CyUSB.ISO_PKT_INFO[] pkts = pktsInfo[k];
''' 
''' 						for (int j = 0; j < this.PPX; j++)
''' 						{
''' 							if (pkts[(System.Int32)(j)].Status == 0)
''' 							{
''' 								this.XferBytes += pkts[(System.Int32)(j)].Length;
''' 
''' 								this.Successes++;
''' 							}
''' 							else
''' 								this.Failures++;
''' 
''' 							pkts[(System.Int32)(j)].Length = 0;
''' 						}
''' 
''' 					}
''' 					else
''' 						this.Failures++;
''' 				}
''' 				else
''' 				{
''' 					// FinishDataXfer
''' 					if (this.EndPoint.FinishDataXfer(ref cBufs[k], ref xBufs[k], ref len, ref oLaps[k]))
''' 					{
''' 						this.XferBytes += len;
''' 						this.Successes++;
''' 						this.frames++;
''' 					}
''' 					else
''' 						this.Failures++;
''' 				}
''' 
''' 				// Re-submit this buffer into the queue
''' 				len = this.BufSz;
''' 				if (this.EndPoint.BeginDataXfer(ref cBufs[k], ref xBufs[k], ref len, ref oLaps[k]) == false)
''' 					this.Failures++;
''' 
''' 
''' 				this.sample = xBufs[k];
''' 
''' 
''' 
''' 				// do something with your image, e.g. save it to disc
''' 
''' 
''' 				//Invoke(new MethodInvoker(showDataAsImage));
''' 				//showDataAsImage(sample);
''' 				//Thread.Sleep(100);
''' 
''' 				k++;
''' 				if (k == this.QueueSz)  // Only update displayed stats once each time through the queue
''' 				{
''' 					k = 0;
''' 
''' 					this.t2 = System.DateTime.Now;
''' 					this.elapsed = this.t2 - this.t1;
''' 
''' 					this.fps = this.frames;//(int) (frames / elapsed.TotalSeconds);
''' 					this.frames = 0;
''' 					this.t1 = this.t2;
''' 
'''                     /*xferRate = (long)(XferBytes / elapsed.TotalMilliseconds);
''' 					xferRate = xferRate / (int)100 * (int)100;*/
''' 
''' 
'''                     // Call StatusUpdate() in the main thread
'''                     if (Streamer.Form1.bRunning == true) this.Invoke(this.updateUI);
''' 
''' 					// For small QueueSz or PPX, the loop is too tight for UI thread to ever get service.   
''' 					// Without this, app hangs in those scenarios.
''' 					System.Threading.Thread.Sleep(0);
''' 				}
''' 				System.Threading.Thread.Sleep(0);
''' 
''' 			} // End infinite loop
''' 			// Let's recall all the queued buffer and abort the end point.
''' 			this.EndPoint.Abort();
''' 		}
''' 
''' 

        ' Summary
        ' 		  The callback routine delegated to updateUI.
        ' 		
        Public Sub StatusUpdate()

            showDataAsImage()
        End Sub


        ' Summary
        ' 		  The callback routine delegated to handleException.
        ' 		
        Public Sub ThreadException()
            StartBtn.Text = "Start"
            bRunning = False

            ' t2 = DateTime.Now;
            ' 			elapsed = t2 - t1;
            ' 			xferRate = (long)(XferBytes / elapsed.TotalMilliseconds);
            ' 			xferRate = xferRate / (int)100 * (int)100;

            tListen = Nothing

            StartBtn.BackColor = Color.Aquamarine

        End Sub

        Private Sub PerfTimer_Tick(sender As Object, e As EventArgs)

        End Sub

        Private Sub sendData()
            ' usbDevices = new USBDeviceList(CyConst.DEVICES_CYUSB);
            ' 			myDevice = usbDevices[0x04B4, 0x1003] as CyUSBDevice;

            If MyDevice IsNot Nothing Then     'Enter this loop only if device is attached
                'Assigning the object
                CtrlEndPt = MyDevice.ControlEndPt
                Dim len = 0
                Dim bufBegin = New Byte() {&H00, &H00, &H00}

                'Vendor Command Format : 0xAA to configure the Image Sensor and add the Header
                CtrlEndPt.Target = CyConst.TGT_DEVICE
                CtrlEndPt.ReqType = CyConst.REQ_VENDOR
                CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE
                CtrlEndPt.ReqCode = VX_AA                               ' to configure Image sensor
                CtrlEndPt.Value = 0
                CtrlEndPt.Index = 0
                CtrlEndPt.XferData(bufBegin, len)              'asking for the 512 bytes buffer containing the 5 bytes Header
                ' Thread.Sleep(20);
                ' Open the file for reading
                Using sr As StreamReader = New StreamReader(configFile)
                    Dim line As String

                    ' Read and display lines until the end of the file
                    While Not Equals((CSharpImpl.__Assign(line, sr.ReadLine())), Nothing)
                        Dim splitted = line.Split(","c)
                        Dim first As String = splitted(0).Trim()
                        Dim Second As String = splitted(1).Trim()

                        Dim matches = Regex.Matches(first, "\b0x[0-9A-Fa-f]+\b")

                        Dim numericPhone As String = New [String](Second.TakeWhile(New Func(Of Char, Boolean)(AddressOf Char.IsDigit)).ToArray())
                        'string numericPhone = new string(Second.Take(6).ToArray());
                        Dim decimalValue = System.Convert.ToInt32(numericPhone, 10)
                        ' Process each line here
                        ' Find decimal numbers in the line using regular expressions
                        'MatchCollection matches = Regex.Matches(line, @"\b\d+\b");

                        ' Convert decimal numbers to hexadecimal
                        For Each match As Match In matches
                            Dim cap = match.Value.Substring(2)
                            Dim dec = System.Convert.ToInt32(cap, 16)
                            'string hexValue = decimalValue.ToString("X");
                            bufBegin(0) = System.Convert.ToByte(dec And &H00FF)
                            bufBegin(1) = System.Convert.ToByte((decimalValue And &HFF00) >> 8)
                            bufBegin(2) = System.Convert.ToByte(decimalValue And &H00FF)
                            len = 0X03
                            'Vendor Command Format : 0xFE to configure the Image Sensor and add the Header
                            CtrlEndPt.Target = CyConst.TGT_DEVICE
                            CtrlEndPt.ReqType = CyConst.REQ_VENDOR
                            CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE
                            CtrlEndPt.ReqCode = VX_FE                               ' to configure Image sensor
                            CtrlEndPt.Value = 0
                            CtrlEndPt.Index = 0
                            CtrlEndPt.XferData(bufBegin, len)
                            'statusBar1.Text = bufBegin.ToString();
                        Next
                        ' Console.WriteLine(line);
                    End While
                    'Vendor Command Format : 0xF5 to start stream from the Image Sensor and add the Header
                    len = 1
                    bufBegin(0) = &H01
                    CtrlEndPt.Target = CyConst.TGT_DEVICE
                    CtrlEndPt.ReqType = CyConst.REQ_VENDOR
                    CtrlEndPt.Direction = CyConst.DIR_FROM_DEVICE
                    CtrlEndPt.ReqCode = VX_F5                               ' to configure Image sensor
                    CtrlEndPt.Value = 0
                    CtrlEndPt.Index = 0
                    CtrlEndPt.XferData(bufBegin, len)

                    Thread.Sleep(0)
                End Using
            End If
        End Sub


        Private x, y, prevX, prevY As Integer, longPress As Integer = 0
        Private watch As Stopwatch
        Private elapsedMs As Long = 0

        Private count As Integer = 0
        Private Sub button3_Click(sender As Object, e As EventArgs)
            setVBlanking(33)
            pixelClkControl()
            'Reset();
        End Sub

        Private vBlank As Integer = 33
        Private Sub setVBlanking(vB As Integer)
            'int vBlank = Convert.ToInt32(txtVBlank.Text);
            Dim bufBegin As Byte() = {&H00, &H00, &H00}

            Dim dec = System.Convert.ToInt32(5)
            bufBegin(0) = System.Convert.ToByte(&H05 And &H00FF)
            bufBegin(1) = System.Convert.ToByte((vB And &HFF00) >> 8)
            bufBegin(2) = System.Convert.ToByte(vB And &H00FF)
            Dim len = 0X03
            'Vendor Command Format : 0xFE to configure the Image Sensor and add the Header
            CtrlEndPt.Target = CyConst.TGT_DEVICE
            CtrlEndPt.ReqType = CyConst.REQ_VENDOR
            CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE
            CtrlEndPt.ReqCode = VX_FE                               ' to configure Image sensor
            CtrlEndPt.Value = 0
            CtrlEndPt.Index = 0
            CtrlEndPt.XferData(bufBegin, len)
            vBlank += 10
            If vBlank > 820 Then
                vBlank = 31
            End If
        End Sub

        Private pxlClk As Integer = 0
        Private Sub pixelClkControl()
            Dim vBlank = System.Convert.ToInt32(txtVBlank.Text)
            Dim bufBegin As Byte() = {&H00, &H00, &H00}

            Dim dec = System.Convert.ToInt32(72)
            bufBegin(0) = System.Convert.ToByte(dec And &H00FF)
            bufBegin(1) = System.Convert.ToByte((vBlank And &HFF00) >> 8)
            bufBegin(2) = System.Convert.ToByte(vBlank And &H00FF)
            Dim len = 0X03
            'Vendor Command Format : 0xFE to configure the Image Sensor and add the Header
            CtrlEndPt.Target = CyConst.TGT_DEVICE
            CtrlEndPt.ReqType = CyConst.REQ_VENDOR
            CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE
            CtrlEndPt.ReqCode = VX_FE                               ' to configure Image sensor
            CtrlEndPt.Value = 0
            CtrlEndPt.Index = 0
            CtrlEndPt.XferData(bufBegin, len)
            If pxlClk = 0 Then
                pxlClk = 1
            Else
                pxlClk = 0
            End If
        End Sub

        Private resetField As Integer = 0
        Private Sub Reset()
            Dim bufBegin As Byte() = {&H00, &H00, &H00}

            'int dec = Convert.ToInt32(0C);
            bufBegin(0) = System.Convert.ToByte(&H0C And &H00FF)
            bufBegin(1) = System.Convert.ToByte((resetField And &HFF00) >> 8)
            bufBegin(2) = System.Convert.ToByte(resetField And &H00FF)
            Dim len = 0X03
            'Vendor Command Format : 0xFE to configure the Image Sensor and add the Header
            CtrlEndPt.Target = CyConst.TGT_DEVICE
            CtrlEndPt.ReqType = CyConst.REQ_VENDOR
            CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE
            CtrlEndPt.ReqCode = VX_FE                               ' to configure Image sensor
            CtrlEndPt.Value = 0
            CtrlEndPt.Index = 0
            CtrlEndPt.XferData(bufBegin, len)
            If resetField = 0 Then
                resetField = 1
            Else
                resetField = 0
            End If
        End Sub

        Private t3 As Windows.Forms.Timer

        Private Sub Form1_KeyDown(sender As Object, e As KeyEventArgs)
            If e.KeyCode = Keys.S Then
                start()
            End If
            If e.KeyCode = Keys.L Then
                lensSwitch()
            End If
        End Sub

        Private robot As Robot
        Private rightClick As Boolean = False, drag As Boolean = False
        Private Sub showDataAsImage()
            Try
                'SensorView.Image = null;

                fps = CInt(fps / elapsed.TotalSeconds)
                If fps < 60 Then
                    setVBlanking(33)
                    'return;
                    'pixelClkControl();
                End If

                label4.Text = elapsed.TotalSeconds.ToString() & " seconds"

                label7.Text = fps.ToString() & " frames"
                ' create a bitmapdata and lock all pixels to be written 
                bmpData = bmp.LockBits(New Rectangle(0, 0, Wd, Ht), ImageLockMode.WriteOnly, bmp.PixelFormat)

                ' copy the data from the byte array into bitmapdata.scan0
                Marshal.Copy(sample, 0, bmpData.Scan0, sample.Length)

                ' unlock the pixels
                bmp.UnlockBits(bmpData)
                SensorView.Image = bmp

                'bmp.Save("C:\\Users\\" + Environment.UserName + "\\Videos\\img.jpg", ImageFormat.Jpeg);


                Dim co_ordinates = String.Empty
                Dim xAxis = 0, yAxis = 0

                Dim t = emguContour(bmp)

                If t.Item1 > 0 Then
                    setVBlanking(System.Convert.ToInt32(txtVBlank.Text))
                    pixelClkControl()
                    xAxis = t.Item1
                    yAxis = t.Item2
                    'MessageBox.Show(xAxis + "," + yAxis);

                    x = System.Convert.ToInt32(KX1 * xAxis + KX2 * yAxis + KX3 + 0.5)
                    y = System.Convert.ToInt32(KY1 * xAxis + KY2 * yAxis + KY3 + 0.5)

                    If watch IsNot Nothing Then
                        elapsedMs = watch.ElapsedMilliseconds
                    End If


                    'robot.mouseMove(x, y);

                    Dim diffX = x - prevX
                    Dim diffY = y - prevY
                    If elapsedMs > 0 AndAlso elapsedMs < 300 Then
                        If elapsedMs > 285 Then
                            'robot.mouseRelease(InputEvent.BUTTON1_MASK);
                            'goto valid;
                        End If
                        If diffX > -2 AndAlso diffX < 2 AndAlso diffY > -2 AndAlso diffY < 2 Then
                            longPress += 1
                            If longPress >= 7 Then
                                robot.mouseMove(x, y)
                                robot.mousePress(java.awt.[event].InputEvent.BUTTON3_MASK)
                                robot.mouseRelease(java.awt.[event].InputEvent.BUTTON3_MASK)
                                robot.delay(1000)
                                longPress = 0
                                rightClick = True
                            End If
                            GoTo valid
                        ElseIf elapsedMs < 80 Then
                        ElseIf diffX >= 0 AndAlso diffX < 15 AndAlso Not rightClick Then
                            If elapsedMs < 150 AndAlso diffX > 0 AndAlso diffX < 10 Then
                                robot.mouseMove(x, y)
                                robot.mousePress(java.awt.[event].InputEvent.BUTTON1_MASK)
                                robot.delay(20)
                                'robot.mouseRelease(InputEvent.BUTTON1_MASK);
                                longPress = 0
                                GoTo valid
                            Else
                                'robot.mouseRelease(InputEvent.BUTTON1_MASK);
                                robot.mousePress(java.awt.[event].InputEvent.BUTTON1_MASK)
                                robot.mouseMove(x, y)
                                robot.delay(10)
                                robot.mouseRelease(java.awt.[event].InputEvent.BUTTON1_MASK)
                                longPress = 0
                                GoTo valid
                            End If

                        End If

                        GoTo invalid
                    ElseIf elapsedMs >= 300 AndAlso Not rightClick Then
                        robot.mouseMove(x, y)
                        robot.mousePress(java.awt.[event].InputEvent.BUTTON1_MASK)
                        robot.delay(50)
                        robot.mouseRelease(java.awt.[event].InputEvent.BUTTON1_MASK)
                        longPress = 0
                    ElseIf rightClick Then
                        rightClick = False
                    End If

                    watch = Stopwatch.StartNew()
                End If

valid:
                prevX = x
                prevY = y

invalid:
                'do nothing


                Dim str = "do nothing"
            Catch ex As Exception
                'MessageBox.Show(ex.ToString() + "\r\n" + "Inner:" + ex.InnerException);
            End Try
        End Sub

        Private Function emguContour(bitmap As Bitmap) As Tuple(Of Integer, Integer)
            Dim image = bitmap.ToImage(Of Bgr, Byte)()
            Dim gray = image.Convert(Of Gray, Byte)().ThresholdBinary(New Gray(230), New Gray(255))
            Dim contours As VectorOfVectorOfPoint = New VectorOfVectorOfPoint()
            Dim m As Emgu.CV.Mat = New Emgu.CV.Mat()
            CvInvoke.FindContours(gray, contours, m, RetrType.External, ChainApproxMethod.ChainApproxSimple)

            For i = contours.Size - 1 To 0 Step -1
                'int radius = contours[i].Size;

                Dim perimeter = CvInvoke.ArcLength(contours(i), True)
                Dim approx As VectorOfPoint = New VectorOfPoint()
                CvInvoke.ApproxPolyDP(contours(i), approx, 0.15 * perimeter, True)
                Call CvInvoke.DrawContours(image, contours, i, New MCvScalar(0, 0, 255), 2)


                'image.Save("C:\\Users\\" + Environment.UserName + "\\Videos\\img_1.jpg");

                'moments  center of the shape
                If approx.Size >= 2 Then
                    Dim moments = CvInvoke.Moments(contours(i))
                    Dim x As Integer = moments.M10 / moments.M00
                    Dim y As Integer = moments.M01 / moments.M00
                    Return Tuple.Create(x, y)
                End If
            Next
            Return Tuple.Create(0, 0)
        End Function

        Private Function identify() As Boolean
            Using src = New Mat("C:\Users\" & Environment.UserName & "\Videos\img.jpg")
                Using gray = New Mat()
                    Using bw = src.CvtColor(ColorConversionCodes.BGR2GRAY) ' convert to grayscale
                        ' invert b&w (specific to your white on black image)
                        Cv2.BitwiseNot(bw, gray)
                    End Using
                    Cv2.GaussianBlur(gray, gray, New OpenCvSharp.Size(Wd, Ht / 3), 0)

                    Dim m As Mat = New Mat()
                    ' find all contours
                    Dim contours = gray.FindContoursAsArray(RetrievalModes.List, ContourApproximationModes.ApproxSimple)




                    Using dst = src.Clone()
                        For Each contour In contours
                            ' filter small contours by their area
                            Dim area = Cv2.ContourArea(contour)
                            If area < 5 * 5 Then Continue For ' a rect of 15x15, or whatever you see fit

                            ' also filter the whole image contour (by 1% close to the real area), there may be smarter ways...
                            If Math.Abs((area - src.Width * src.Height) / area) < 0.01F Then Continue For

                            Dim hull = Cv2.ConvexHull(contour)
                            Cv2.Polylines(dst, {hull}, True, Scalar.Red, 2)

                            Cv2.Moments(contour)

                        Next

                        'using (new Window("src image", src))
                        Dim pic = MatToBitmap(dst)
                        pic.Save("C:\Users\" & Environment.UserName & "\Videos\img_cntr.jpg")

                    End Using
                    If contours.Length > 2 Then
                        Return True
                    End If
                End Using
            End Using
            Return False
        End Function

        Private Shared Function MatToBitmap(mat As Mat) As Bitmap
            Using ms = mat.ToMemoryStream()
                Return CType(Image.FromStream(ms), Bitmap)
            End Using
        End Function

        Private lensemode As Boolean = False

        Private Sub button1_Click(sender As Object, e As EventArgs)
            lensSwitch()
        End Sub

        Private Sub button2_Click(sender As Object, e As EventArgs)
            Close()
            Dim th As Thread = New Thread(AddressOf openNewForm)
            th.SetApartmentState(ApartmentState.STA)
            th.Start()
        End Sub

        Private Sub openNewForm()
            Call Application.Run(New Streamer.ManualCaliberation())
        End Sub

        Private Sub lensSwitch()
            Dim bufBegin2 = New Byte() {&H01}
            Dim len = 1
            If lensemode = False Then
                CtrlEndPt.Target = CyConst.TGT_DEVICE
                CtrlEndPt.ReqType = CyConst.REQ_VENDOR
                CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE
                CtrlEndPt.ReqCode = VX_FC
                CtrlEndPt.Value = &H0040
                CtrlEndPt.Index = 0
                CtrlEndPt.XferData(bufBegin2, len)
                Thread.Sleep(50)
                bufBegin2(0) = &H00
                CtrlEndPt.Target = CyConst.TGT_DEVICE
                CtrlEndPt.ReqType = CyConst.REQ_VENDOR
                CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE
                CtrlEndPt.ReqCode = VX_FC
                CtrlEndPt.Value = &H0040
                CtrlEndPt.Index = 0
                CtrlEndPt.XferData(bufBegin2, len)

                lensemode = True
            Else
                CtrlEndPt.Target = CyConst.TGT_DEVICE
                CtrlEndPt.ReqType = CyConst.REQ_VENDOR
                CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE
                CtrlEndPt.ReqCode = VX_FC
                CtrlEndPt.Value = &H0080
                CtrlEndPt.Index = 0
                CtrlEndPt.XferData(bufBegin2, len)
                Thread.Sleep(50)
                bufBegin2(0) = &H00
                CtrlEndPt.Target = CyConst.TGT_DEVICE
                CtrlEndPt.ReqType = CyConst.REQ_VENDOR
                CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE
                CtrlEndPt.ReqCode = VX_FC
                CtrlEndPt.Value = &H0080
                CtrlEndPt.Index = 0
                CtrlEndPt.XferData(bufBegin2, len)

                lensemode = False
            End If
        End Sub

        Private Class CSharpImpl
            <Obsolete("Please refactor calling code to use normal Visual Basic assignment")>
            Shared Function __Assign(Of T)(ByRef target As T, value As T) As T
                target = value
                Return value
            End Function
        End Class

    End Class
End Namespace
