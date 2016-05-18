﻿//------------------------------------------------------------------------------
// <copyright file="MainWindow.xaml.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------
namespace Microsoft.Samples.Kinect.ColorBasics
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Windows;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using System.Runtime.InteropServices;
    using Microsoft.Kinect;
    using System.Threading.Tasks;    

    using System.Linq;

    using Emgu.CV;
    using Emgu.CV.Structure;
    using Emgu.CV.CvEnum;
    using Emgu.CV.Util;    

    /// <summary>
    /// Interaction logic for MainWindow
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        /// <summary>
        /// Width of the depth/infrared image in pixels
        /// </summary>
        const int imageSizeX = 512;
        /// <summary>
        /// Height of the depth/infrared image in pixels
        /// </summary>
        const int imageSizeY = 424;
        IntPtr convertedColorDataPtr = IntPtr.Zero;
		IntPtr convertedInfraredDataPtr = IntPtr.Zero;
        double cannyThreshold = 200.0;
        double cannyThresholdLinking = 200.0;
        double infraMultiplier = 3.0;
        byte[] filterHsv = new byte[] { 60, 255, 255 };
        /// <summary>
        /// Pixel position of the point that needs to be inspected in the next frame
        /// </summary>
        System.Drawing.Point? inspectPosition = null;
        /// <summary>
        /// Array holding the data needed for the screen->world transformation
        /// </summary>
        PointF[] cameraSpaceTable = null;

        /// <summary>
        /// Active Kinect sensor
        /// </summary>
        private KinectSensor kinectSensor = null;

        /// <summary>
        /// Reader for color frames
        /// </summary>
        /// 
        private MultiSourceFrameReader multiSourceFrameReader = null;
        private ColorFrameReader colorFrameReader = null;
        private InfraredFrameReader infraredFrameReader = null;
        private DepthFrameReader depthFrameReader = null;
        /// <summary>
        /// Bitmap to display
        /// </summary>
        private WriteableBitmap colorBitmap = null;

        /// <summary>
        /// Current status text to display
        /// </summary>
        private string statusText = null;

        /// <summary>
        /// Initializes a new instance of the MainWindow class.
        /// </summary>
        public MainWindow()
        {            
            // get the kinectSensor object
            this.kinectSensor = KinectSensor.GetDefault();

            // open the reader for the color frames
            this.colorFrameReader = this.kinectSensor.ColorFrameSource.OpenReader();
            this.infraredFrameReader = this.kinectSensor.InfraredFrameSource.OpenReader();
            this.depthFrameReader = this.kinectSensor.DepthFrameSource.OpenReader();
            this.multiSourceFrameReader = this.kinectSensor.OpenMultiSourceFrameReader(FrameSourceTypes.Infrared | FrameSourceTypes.Depth);

            // wire handler for frame arrival
            this.colorFrameReader.FrameArrived += this.Reader_ColorFrameArrived;
            this.infraredFrameReader.FrameArrived += this.Reader_InfraredFrameArrived;
            this.depthFrameReader.FrameArrived += this.Reader_DepthFrameArrived;
            this.multiSourceFrameReader.MultiSourceFrameArrived += this.Reader_MultiSourceFrameArrived;
            // create the colorFrameDescription from the ColorFrameSource using Bgra format
            FrameDescription colorFrameDescription = this.kinectSensor.ColorFrameSource.CreateFrameDescription(ColorImageFormat.Bgra);

            // create the bitmap to display
            this.colorBitmap = new WriteableBitmap(imageSizeX, imageSizeY, 96.0, 96.0, PixelFormats.Bgr24, null);
            //this.colorBitmap = new WriteableBitmap(colorFrameDescription.Width / 2, colorFrameDescription.Height / 2, 96.0, 96.0, PixelFormats.Bgr32, null);
            // set IsAvailableChanged event notifier
            this.kinectSensor.IsAvailableChanged += this.Sensor_IsAvailableChanged;

            // open the sensor
            this.kinectSensor.Open();

            // set the status text
            this.StatusText = this.kinectSensor.IsAvailable ? Properties.Resources.RunningStatusText
                                                            : Properties.Resources.NoSensorStatusText;

            // use the window object as the view model in this simple example
            this.DataContext = this;

            // initialize the components (controls) of the window
            this.InitializeComponent();

            CannyThreshold = 100;
            CannyThresholdLinking = 100;
            kinectSensor.CoordinateMapper.CoordinateMappingChanged += CoordinateMappingChangedCallback;
        }        

        /// <summary>
        /// INotifyPropertyChangedPropertyChanged event to allow window controls to bind to changeable data
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Gets the bitmap to display
        /// </summary>
        public ImageSource ImageSource
        {
            get
            {
                return this.colorBitmap;
            }
        }

        public double CannyThreshold
        {
            get
            {
                return this.cannyThreshold;
            }
            set
            {
                this.cannyThreshold = value;
                this.cannyThresholdTextBox.Text = value.ToString();
            }
        }
        public double CannyThresholdLinking
        {
            get
            {
                return this.cannyThresholdLinking;
            }
            set
            {
                this.cannyThresholdLinking = value;
                this.cannyThresholdLinkingTextBox.Text = cannyThresholdLinking.ToString();
            }
        }
        public double InfraMultiplier
        {
            get
            {
                return infraMultiplier;
            }
            set
            {
                infraMultiplier = value;
            }
        }

        /// <summary>
        /// Gets or sets the current status text to display
        /// </summary>
        public string StatusText
        {
            get
            {
                return this.statusText;
            }

            set
            {
                if (this.statusText != value) {
                    this.statusText = value;

                    // notify any bound elements that the text has changed
                    if (this.PropertyChanged != null) {
                        this.PropertyChanged(this, new PropertyChangedEventArgs("StatusText"));
                    }
                }
            }
        }

        /// <summary>
        /// Execute shutdown tasks
        /// </summary>
        /// <param name="sender">object sending the event</param>
        /// <param name="e">event arguments</param>
        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (this.colorFrameReader != null) {
                // ColorFrameReder is IDisposable
                this.colorFrameReader.Dispose();
                this.colorFrameReader = null;
            }

            if (this.kinectSensor != null) {
                this.kinectSensor.Close();
                this.kinectSensor = null;
            }

            if (convertedColorDataPtr != IntPtr.Zero) {
                Marshal.FreeHGlobal(convertedColorDataPtr);
            }
        }

        /// <summary>
        /// Handles the color frame data arriving from the sensor
        /// </summary>
        /// <param name="sender">object sending the event</param>
        /// <param name="e">event arguments</param>
        private async void Reader_ColorFrameArrived(object sender, ColorFrameArrivedEventArgs e)
        {
            if (!colorCheckBox.IsChecked ?? false) {
                return;
            }
            // ColorFrame is IDisposable
            using (ColorFrame colorFrame = e.FrameReference.AcquireFrame()) {
                if(colorFrame != null) {
                    var processedFrameTuple = await ProcessColorFrame(colorFrame);
                    //CvInvoke.Imshow("original", processedFrameTuple.Item1);
                    DisplayMatOnBitmap(processedFrameTuple.Item1, this.colorBitmap);
                    CvInvoke.Imshow("edges", processedFrameTuple.Item2);
                    processedFrameTuple.Item1.Dispose();
                    processedFrameTuple.Item2.Dispose();
                }
            }
        }

        private void Reader_InfraredFrameArrived(object sender, InfraredFrameArrivedEventArgs e)
        {
            if(!infraredCheckBox.IsChecked ?? false) {
                return;
            }
            using (InfraredFrame infraredFrame = e.FrameReference.AcquireFrame()) {
                var infraredFrameDescription = this.infraredFrameReader.InfraredFrameSource.FrameDescription;

				using (Mat infraredMat = new Mat(infraredFrameDescription.Height, infraredFrameDescription.Width, DepthType.Cv16U, 1)) {

					infraredFrame?.CopyFrameDataToIntPtr(infraredMat.DataPointer, infraredFrameDescription.LengthInPixels * 2);

					TriangleFromInfrared(infraredMat);

					CvInvoke.Imshow("infrared", infraredMat);
				}					
            }
        }

        private void Reader_DepthFrameArrived(object sender, DepthFrameArrivedEventArgs e)
        {
            if(!depthCheckBox.IsChecked ?? false)
                return;                        

            using(DepthFrame depthFrame = e.FrameReference.AcquireFrame()) {

                if (depthFrame == null)
                    return;

                FrameDescription depthFrameDescription = depthFrame.FrameDescription;

                using (Mat depthMat = new Mat(depthFrameDescription.Height, depthFrameDescription.Width, DepthType.Cv16U, 1))
                using (Mat convertedMat = new Mat(depthFrameDescription.Height, depthFrameDescription.Width, DepthType.Cv8U, 1))
                {

                    depthFrame.CopyFrameDataToIntPtr(depthMat.DataPointer, depthFrameDescription.BytesPerPixel * depthFrameDescription.LengthInPixels);
                    depthMat.ConvertTo(convertedMat, DepthType.Cv8U, 1 / 256d);
                    CvInvoke.Imshow("depth", convertedMat);
                }
            }
        }

        /// <summary>
        /// Handles the event which the sensor becomes unavailable (E.g. paused, closed, unplugged).
        /// </summary>
        /// <param name="sender">object sending the event</param>
        /// <param name="e">event arguments</param>
        private void Sensor_IsAvailableChanged(object sender, IsAvailableChangedEventArgs e)
        {
            // on failure, set the status text
            this.StatusText = this.kinectSensor.IsAvailable ? Properties.Resources.RunningStatusText
                                                            : Properties.Resources.SensorNotAvailableStatusText;
        }
				
		private Triangle2DF TriangleFromInfrared(Mat infraredMat)
		{
			Image<Gray, short> infraredImg = infraredMat.ToImage<Gray, short>();
			var smoothedInfraredImg = infraredImg.PyrDown();
			smoothedInfraredImg = smoothedInfraredImg.PyrUp();					

			using (Mat convertedMat = new Mat(infraredMat.Size, DepthType.Cv8U, 1))
            using (Mat multiplierMat = new Mat(infraredMat.Size, DepthType.Cv8U, 1))
            {
                infraredImg.Mat.ConvertTo(convertedMat, DepthType.Cv8U, 1d / 256d);

                multiplierMat.SetTo(new MCvScalar(infraMultiplier));

                CvInvoke.Multiply(convertedMat, multiplierMat, convertedMat);
                //CvInvoke.Imshow("infrared -> converted", convertedMat);

                Mat thresholdMat = new Mat(infraredMat.Size, DepthType.Cv8U, 1);
                CvInvoke.Threshold(convertedMat, thresholdMat, 230, 255, ThresholdType.Binary);
                //CvInvoke.Imshow("infrared -> converted -> threshold", thresholdMat);

                //CvInvoke.Canny(thresholdMat, cannyMat, cannyThreshold, cannyThresholdLinking);

                #region triangles
                List<Triangle2DF> triangleList = new List<Triangle2DF>();

                using (VectorOfVectorOfPoint contours = new VectorOfVectorOfPoint()) {
                    CvInvoke.FindContours(thresholdMat, contours, null, RetrType.List, ChainApproxMethod.ChainApproxSimple);
                    int count = contours.Size;
                    for (int i = 0; i < count; i++) {
                        using (VectorOfPoint contour = contours[i])
                        using (VectorOfPoint approxContour = new VectorOfPoint()) {
                            CvInvoke.ApproxPolyDP(contour, approxContour, CvInvoke.ArcLength(contour, true) * 0.05, true);
                            if (CvInvoke.ContourArea(approxContour, false) > 250) {
                                if (approxContour.Size == 3) {
                                    var pts = approxContour.ToArray();
                                    triangleList.Add(new Triangle2DF(pts[0], pts[1], pts[2]));
                                }
                            }
                        }
                    }
                }

                var biggestTri = triangleList.OrderBy((tri) => tri.Area).FirstOrDefault();
                //CvInvoke.Polylines(thresholdMat, Array.ConvertAll(biggestTri.GetVertices(), System.Drawing.Point.Round), true, new MCvScalar(255));

                //foreach (var triangle in triangleList) {
                //    CvInvoke.Polylines(cannyMat, Array.ConvertAll(triangle.GetVertices(), System.Drawing.Point.Round), true, new MCvScalar(255));
                //}            
                //CvInvoke.Imshow("threshold", thresholdMat);

                return biggestTri;
                
                #endregion
            }
        }

		private async Task<Tuple<Mat, Mat>> ProcessColorFrame (ColorFrame colorFrame)
        {
            return await Task.Run(() =>
            {
                FrameDescription colorFrameDescription = colorFrame.FrameDescription;

                using (KinectBuffer colorBuffer = colorFrame.LockRawImageBuffer()) {

                    if (convertedColorDataPtr == IntPtr.Zero) {
                        convertedColorDataPtr = Marshal.AllocHGlobal(4 * (int)colorFrameDescription.LengthInPixels);
                    }

                    colorFrame.CopyConvertedFrameDataToIntPtr(convertedColorDataPtr, 4 * colorFrameDescription.LengthInPixels, ColorImageFormat.Bgra);

                    Mat resizedImage = new Mat();
                    using (Mat convertedImage_Bgr = new Mat(colorFrameDescription.Height, colorFrameDescription.Width, DepthType.Cv8U, 3))
                    using (Mat convertedImage_Bgra = new Mat(colorFrameDescription.Height, colorFrameDescription.Width, DepthType.Cv8U, 4, convertedColorDataPtr, colorFrameDescription.Width * 4)) {

                        CvInvoke.CvtColor(convertedImage_Bgra, convertedImage_Bgr, ColorConversion.Bgra2Bgr);
                        CvInvoke.Resize(convertedImage_Bgr, resizedImage, new System.Drawing.Size(imageSizeX, imageSizeY));


                        if(inspectPosition != null) {
                            InspectPixel(resizedImage);
                        }
                    }
                    //return Tuple.Create(resizedImage, CannyShapeDetection(resizedImage));
                    return Tuple.Create(resizedImage, ChromaShapeDetection(resizedImage));
                }
            });            
        }

        private Mat CannyShapeDetection(Mat frame)
        {
            Mat returnImg = new Mat(frame.Rows, frame.Cols, frame.Depth, frame.NumberOfChannels);
            CvInvoke.Canny(frame, returnImg, cannyThreshold, cannyThresholdLinking);                                    

            List<Triangle2DF> triangleList = new List<Triangle2DF>();

            using (VectorOfVectorOfPoint contours = new VectorOfVectorOfPoint()) {
                CvInvoke.FindContours(returnImg, contours, null, RetrType.List, ChainApproxMethod.ChainApproxSimple);
                int count = contours.Size;
                for (int i = 0; i < count; i++) {
                    using (VectorOfPoint contour = contours[i])
                    using (VectorOfPoint approxContour = new VectorOfPoint()) {
                        CvInvoke.ApproxPolyDP(contour, approxContour, CvInvoke.ArcLength(contour, true) * 0.05, true);
                        if (CvInvoke.ContourArea(approxContour, false) > 250) {
                            if (approxContour.Size == 3) {
                                var pts = approxContour.ToArray();
                                triangleList.Add(new Triangle2DF(pts[0], pts[1], pts[2]));
                            }
                        }
                    }
                }
            }
            foreach (var triangle in triangleList) {
                CvInvoke.Polylines(returnImg, Array.ConvertAll(triangle.GetVertices(), System.Drawing.Point.Round), true, new MCvScalar(255));
            }
            
            return returnImg;
        }

        private Mat ChromaShapeDetection(Mat frame)
        {
            Mat chromaFrame = new Mat(frame.Size, frame.Depth, frame.NumberOfChannels);

            MCvScalar lowerLimit = new MCvScalar((filterHsv[0] - 5) % 180, 0, 0);//(filterHsv[1] - 100) % 255, (filterHsv[2] - 100) % 255);
            MCvScalar upperLimit = new MCvScalar((filterHsv[0] + 5) % 180, 255, 255);//(filterHsv[1] + 100) % 255, (filterHsv[2] + 100) % 255);

            using (Mat lowerLimits = new Mat(frame.Size, frame.Depth, frame.NumberOfChannels))
            using (Mat upperLimits = new Mat(frame.Size, frame.Depth, frame.NumberOfChannels))
            using (Mat hsvFrame = new Mat()) {
                CvInvoke.CvtColor(frame, hsvFrame, ColorConversion.Bgr2Hsv);
                lowerLimits.SetTo(lowerLimit);
                upperLimits.SetTo(upperLimit);
                CvInvoke.InRange(hsvFrame, lowerLimits, upperLimits, chromaFrame);
                CvInvoke.MedianBlur(chromaFrame, chromaFrame, 7);                
            }

            return chromaFrame;   
        }

        private void InspectPixel(Mat mat)
        {
            if (inspectPosition == null)
                return;

            var colorPos = inspectPosition ?? new System.Drawing.Point(0, 0);
            inspectPosition = null;

            var image = mat.ToImage<Bgr, byte>();

            var filterBgr = new byte[] { image.Data[colorPos.Y, colorPos.X, 0], image.Data[colorPos.Y, colorPos.X, 1], image.Data[colorPos.Y, colorPos.X, 2] };       

            Mat input = new Mat(1, 1, DepthType.Cv8U, 3);
            Mat output = new Mat(1, 1, DepthType.Cv8U, 3);
            input.SetTo(filterBgr);

            CvInvoke.CvtColor(input, output, ColorConversion.Bgr2Hsv);

            filterHsv = output.GetData();
            Console.WriteLine($"R: {filterBgr[2]}, G: {filterBgr[1]}, B: {filterBgr[0]}");
            Console.WriteLine($"H: {filterHsv[0]}, S: {filterHsv[1]}, V: {filterHsv[2]}");
        }

        private void DisplayMatOnBitmap (Mat mat, WriteableBitmap bitmap)
        {
            bitmap.Lock();

            colorBitmap.WritePixels(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight), mat.DataPointer, bitmap.PixelWidth * bitmap.PixelHeight * 3, mat.Step);
            colorBitmap.AddDirtyRect(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));

            bitmap.Unlock();
        }

        private void Image_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(this.colorImage);
            inspectPosition = new System.Drawing.Point((int)Math.Round(pos.X), (int)Math.Round(pos.Y));                        
        }

        private void Reader_MultiSourceFrameArrived(object sender, MultiSourceFrameArrivedEventArgs e)
        {
            var multiFrame = e.FrameReference.AcquireFrame();

            using (var infraredFrame = multiFrame.InfraredFrameReference.AcquireFrame())
            using (var depthFrame = multiFrame.DepthFrameReference.AcquireFrame())
            {
                if (infraredFrame == null || depthFrame == null)
                    return;

                var frameSize = new System.Drawing.Size(infraredFrame.FrameDescription.Width, infraredFrame.FrameDescription.Height);
                var frameDataLength = infraredFrame.FrameDescription.BytesPerPixel * infraredFrame.FrameDescription.LengthInPixels;

                using (Mat triMaskMat = new Mat(frameSize, DepthType.Cv8U, 1))
                using (Mat infraredMat = new Mat(frameSize, DepthType.Cv16U, 1))
                using (Mat maskedInfraredMat = new Mat(frameSize, DepthType.Cv16U, 1))
                using (Mat depthMat = new Mat(frameSize, DepthType.Cv16U, 1)) {
                    infraredFrame.CopyFrameDataToIntPtr(infraredMat.DataPointer, frameDataLength);
                    depthFrame.CopyFrameDataToIntPtr(depthMat.DataPointer, frameDataLength);
                    triMaskMat.SetTo(new MCvScalar(0));
                    maskedInfraredMat.SetTo(new MCvScalar(0));

                    var tri = TriangleFromInfrared(infraredMat);

                    //CvInvoke.FillConvexPoly(triMaskMat, new VectorOfPoint(Array.ConvertAll(tri.GetVertices(), System.Drawing.Point.Round)), new MCvScalar(255));

                    DisplayDepthHSV(depthMat);
                    CalculateNormal(depthMat, tri);
                    CvInvoke.Imshow("triangle", DisplayTriangleOnInfrared(infraredMat, tri));
                    //CvInvoke.Imshow("infrared", infraredMat);
                    //CvInvoke.Imshow("depth", depthMat);
                }
            }
        }

        private void CalculateNormal(Mat depthMat, Triangle2DF tri)
        {
            if (depthMat == null || (tri.Centeroid.X == 0 && tri.Centeroid.Y == 0))
                return;

            var vertices = Array.ConvertAll(tri.GetVertices(), System.Drawing.Point.Round);
            var point2dList = vertices;// Kolos.haromszog.belsopontok(vertices);

            #region Kolos normalvektor
            //var point3dList = new List<Kolos.pont3d>();

            //foreach (var point2d in point2dList) {
            //    var zcoord = GetMatElementU16(depthMat, point2d.X, point2d.Y);
            //    if(zcoord != 0)
            //        point3dList.Add(CalculateWorldPosition(point2d.X, point2d.Y, depthMat.Cols, zcoord));
            //}

            //var normal = Kolos.normalvektor.kiszamitas(point3dList);
            //Console.WriteLine($"Normálvektor X:{normal[0].ToString("F4")} Y:{normal[1].ToString("F4")}, Z: {normal[2].ToString("F4")}");
            #endregion


            MCvPoint3D32f[] vertices3d = new MCvPoint3D32f[3];
            for (int i = 0; i < 3; i++) {
                var point2d = point2dList[i];
                var zcoord = GetMatElementU16(depthMat, point2d.X, point2d.Y);
                if (zcoord == 0)
                    return;
                var point3d = CalculateWorldPosition(point2d.X, point2d.Y, depthMat.Cols, zcoord);
                vertices3d[i] = new MCvPoint3D32f((float)point3d.x, (float)point3d.y, (float)point3d.z);
            }
            
            var tri3d = new Triangle3DF(vertices3d[0], vertices3d[1], vertices3d[2]);
            var normal = tri3d.Normal;
            Console.WriteLine($"Normal: X:{normal.X} Y:{normal.Y} Z:{normal.Z}");
        }

        private Mat DisplayTriangleOnInfrared(Mat infraredMat, Triangle2DF tri)
        {
            using (Mat convertMat = new Mat(infraredMat.Size, DepthType.Cv8U, 1)) {
                infraredMat.ConvertTo(convertMat, DepthType.Cv8U, 1 / 256d);
                Mat displayMat = new Mat(infraredMat.Size, DepthType.Cv8U, 3);
                CvInvoke.CvtColor(convertMat, displayMat, ColorConversion.Gray2Bgr);
                CvInvoke.Polylines(displayMat, Array.ConvertAll(tri.GetVertices(), System.Drawing.Point.Round), true, new Bgr(0, 0, 255).MCvScalar, 2);
                return displayMat;
            }
        }

        private void DisplayDepthHSV(Mat depthMat16U)
        {
            if (depthMat16U == null)
                return;

            using (Mat convertedMat8U = new Mat(depthMat16U.Size, DepthType.Cv8U, 1))
            using (Mat colorMat8U3 = new Mat(depthMat16U.Size, DepthType.Cv8U, 3))
            using (Mat hsvMat8U3 = new Mat(depthMat16U.Size, DepthType.Cv8U, 3))
            using (Mat hsvConstantMat = new Mat(depthMat16U.Size, DepthType.Cv8U, 3))
            {
                depthMat16U.ConvertTo(convertedMat8U, DepthType.Cv8U, 1 / 256d);
                CvInvoke.CvtColor(convertedMat8U, colorMat8U3, ColorConversion.Gray2Bgr);

                hsvConstantMat.SetTo(new MCvScalar(0, 255, 255));
                CvInvoke.BitwiseOr(colorMat8U3, hsvConstantMat, hsvMat8U3);

                CvInvoke.CvtColor(hsvMat8U3, hsvMat8U3, ColorConversion.Hsv2Bgr);                

                DisplayMatOnBitmap(hsvMat8U3, this.colorBitmap);
                InspectDepthPixel(depthMat16U);
            }
        }

        private void InspectDepthPixel(Mat depthMat16U)
        {
            if (inspectPosition == null || depthMat16U == null)
                return;

            var pos = inspectPosition.Value;
            if (pos.X >= depthMat16U.Cols || pos.Y >= depthMat16U.Rows)
                return;

            //Console.WriteLine($"Depth value at X:{pos.X} Y:{pos.Y} is {GetMatElementU16(depthMat16U, pos.X, pos.Y)}");
            var worldPos = CalculateWorldPosition(pos.X, pos.Y, depthMat16U.Cols, GetMatElementU16(depthMat16U, pos.X, pos.Y));
            Console.WriteLine($"Clicked World pos: X:{worldPos.x} Y:{worldPos.y} Z:{worldPos.z}");
            inspectPosition = null;
        }

        private Kolos.Pont3d CalculateWorldPosition(int screenX, int screenY, int width, ushort depthValue)
        {
            PointF lookupValue = cameraSpaceTable[screenX + screenY * width];
            return new Kolos.Pont3d(lookupValue.X * depthValue, lookupValue.Y * depthValue, depthValue);
        }

        private void CoordinateMappingChangedCallback(object sender, CoordinateMappingChangedEventArgs args)
        {
            cameraSpaceTable = kinectSensor.CoordinateMapper.GetDepthFrameToCameraSpaceTable();
        }

        private unsafe ushort GetMatElementU16(Mat mat, int x, int y)            
        {
            ushort* dataPtr = (ushort*)mat.DataPointer;
            return *(dataPtr + x + mat.Cols * y);
        }
    }
}
