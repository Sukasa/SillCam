using System;
using System.Drawing;
using System.Threading;
using IvyBus;
using System.Configuration;

using RaspberryCam;

namespace SillCam
{
    class Program
    {
        private static int HistorySize;
        private static int SavePeriod;
        private static int RollingPtr = 0;
        private static int SaveTimer = 0;
        private static string OutputFormat;
        private static string Domain;
        private static string DevicePath;
        private static Picture[] Snapshots;
        private static Cameras Camera;
        private static Object LockObj = new Object();
        private static Ivy Ivy;

        private const int FPS = 2;
        private static Int64 TimestampStart;

        static void Main(string[] args)
        {

            // *** Pull configuration information from config file
            Domain = Properties.Settings.Default.Domain;
            DevicePath = Properties.Settings.Default.DevicePath;
            OutputFormat = Properties.Settings.Default.OutputFormat;
            HistorySize = Properties.Settings.Default.HistorySize;
            SavePeriod = Properties.Settings.Default.SavePeriod;

            Snapshots = new Picture[HistorySize];

            // *** Set up Ivy
            Ivy = new Ivy();
            Ivy.AppName = "Sill Camera Controller";
            Ivy.BindMsg("TOKEN_.*", SaveHistory, null);
            Ivy.BindMsg("SILLCAM_CAPTURE", SaveSinglePicture, null);
            Ivy.DebugProtocol = true;
            Ivy.Start(Domain);
            
            // *** Declare camera device and start streaming in video at 1fps
            Camera = Cameras.DeclareDevice().Named("Camera 1").WithDevicePath(DevicePath).Memorize();
            Camera.Get("Camera 1").StartVideoStreaming(new PictureSize(640, 480), FPS);

            while (true)
            {
                Thread.Sleep(1000 / FPS);
                lock (LockObj)
                {
                    TakePicture();

                    // *** If we're in the middle of a rolling capture
                    if (SaveTimer > 0)
                    {
                        // Do another capture
                        SaveTimer--;
                        SavePicture(RollingPtr);

                        // *** After completion, broadcast that we finished
                        if (SaveTimer == 0)
                        {
                            Ivy.SendMsg("SILLCAM_ROLLINGCOMPLETE:{0}:{1}", TimestampStart, Timestamp());
                        }
                    }
                    RollingPtr = (++RollingPtr) % HistorySize;
                }
            }
        }

        // *** Save a single picture from the camera buffer
        static void SaveSinglePicture(object sender, IvyMessageEventArgs e)
        {
            Console.WriteLine("Saving single picture");
            lock (LockObj)
            {
                int Index = (HistorySize - 1 + RollingPtr) % HistorySize;

                Int64 i = SavePicture(Index);
                Ivy.SendMsg("SILLCAM_PICTURE:" + string.Format(OutputFormat, i));
            }
        }

        // *** Save the entire camera buffer, and schedule continuing saves as we take more pictures
        static void SaveHistory(object sender, IvyMessageEventArgs e)
        {
            Console.WriteLine("Saving history");
            int Count = 0;
            lock (LockObj)
            {
                if (SaveTimer == 0)
                {
                    TimestampStart = Timestamp();

                    for (int SaveIndex = RollingPtr; Count < HistorySize; SaveIndex = ++SaveIndex % HistorySize, Count++)
                    {
                        SavePicture(SaveIndex);
                    }
                }
                SaveTimer = SavePeriod;
            }
        }

        // *** Save a picture, and return the picture's capture timestamp
        static Int64 SavePicture(int Index)
        {
            Picture Snapshot = Snapshots[Index];
            if (Snapshot.PictureData == null)
            {
                Console.WriteLine("Failed to save snapshot: no data");
                return Timestamp();
            }

            if (Snapshot.PictureData.Length < 5)
            {
                Console.WriteLine("Failed to save snapshot: truncated data");
                return Timestamp();
            }

            if (Snapshot.Timestamp.Year < 2000)
            {
                Console.WriteLine("Failed to save snapshot: invalid timestamp");
                return Timestamp();
            }

            ImageConverter ic = new ImageConverter();
            try
            {
                Image img = (Image)ic.ConvertFrom(Snapshot.PictureData);

                String Filename = string.Format(OutputFormat, Timestamp(Snapshot.Timestamp));
                img.Save(Filename);

            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR");
                return Timestamp();
            }
            return Timestamp();
        }

        // *** Pull in a picture from the camera stream
        static void TakePicture()
        {
            Picture Snapshot;
            Snapshot.PictureData = Camera.Get("Camera 1").GetVideoFrame();
            Snapshot.Timestamp = DateTime.Now;
            Snapshots[RollingPtr] = Snapshot;
        }

        private struct Picture
        {
            public byte[] PictureData;
            public DateTime Timestamp;
        }

        public static Int64 Timestamp()
        {
            return Timestamp(DateTime.Now);
        }

        public static Int64 Timestamp(DateTime dateTime)
        {
            return (Int64)(dateTime - new DateTime(1970, 1, 1).ToLocalTime()).TotalSeconds;
        }
    }
}
