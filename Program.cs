﻿using System;
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
        private static int FPS = 2;
        private static string OutputFormat;
        private static string Domain;
        private static string DevicePath;
        private static Picture[] Snapshots;
        private static Cameras Camera;
        private static Object LockObj = new Object();
        private static Ivy Ivy;
        private static ImageConverter Converter = new ImageConverter();
        private static Int64 TimestampStart;
        private static Picture[] History;

        static void Main(string[] args)
        {

            // *** Pull configuration information from config file
            Domain = Properties.Settings.Default.Domain;
            DevicePath = Properties.Settings.Default.DevicePath;
            OutputFormat = Properties.Settings.Default.OutputFormat;
            HistorySize = Properties.Settings.Default.HistorySize;
            SavePeriod = Properties.Settings.Default.SavePeriod;
            FPS = Properties.Settings.Default.FPS;

            Snapshots = new Picture[HistorySize];
            History = new Picture[HistorySize];

            // *** Set up Ivy
            Ivy = new Ivy();
            Ivy.AppName = "Sill Camera Controller";
            Ivy.BindMsg("TOKEN_.*", SaveHistory, null);
            Ivy.BindMsg("SILLCAM_CAPTURE", SaveSinglePicture, null);
            Ivy.DebugProtocol = true;
            Ivy.Start(Domain);

            // *** Declare camera device and start streaming in video at 1fps
            Camera = Cameras.DeclareDevice().Named("Camera 1").WithDevicePath(DevicePath).Memorize();
            Camera.Get("Camera 1").StartVideoStreaming(new PictureSize(480, 360), FPS);
            DateTime Time = DateTime.Now;
            DateTime WaitUntil = Time.AddMilliseconds(1000 / FPS);

            while (true)
            {
                Time = DateTime.Now;
                if (Time < WaitUntil)
                    Thread.Sleep((WaitUntil - Time).Milliseconds);
                WaitUntil = Time.AddMilliseconds(1000 / FPS);
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
                            WriteTime();
                            Console.WriteLine("Finished rolling save");
                        }
                    }
                    RollingPtr = (++RollingPtr) % HistorySize;
                }
            }
        }

        // *** Save a single picture from the camera buffer
        static void SaveSinglePicture(object sender, IvyMessageEventArgs e)
        {
            WriteTime();
            Console.WriteLine("Saving single picture");
            lock (LockObj)
            {
                try
                {
                    int Index = (HistorySize - 1 + RollingPtr) % HistorySize;

                    Int64 i = SavePicture(Index);
                    Ivy.SendMsg("SILLCAM_PICTURE:" + string.Format(OutputFormat, i / 1000, i % 1000));
                }
                catch (Exception ex)
                {
                    WriteTime();
                    Console.WriteLine(ex.Message);
                    Console.WriteLine(ex.StackTrace);
                    Console.WriteLine((HistorySize - 1 + RollingPtr) % HistorySize);
                }
            }
        }

        static void SaveHistoryWorker(object History)
        {
            Picture[] Pictures = (Picture[])History;
            foreach (Picture Snapshot in Pictures)
            {
                if (Snapshot.PictureData == null)
                    continue;
                Int64 _Timestamp = Timestamp(Snapshot.Timestamp);
                String Filename = string.Format(OutputFormat, _Timestamp / 1000, _Timestamp % 1000);
                Image img = (Image)Converter.ConvertFrom(Snapshot.PictureData);
                Thread.Sleep(100);
                img.Save(Filename);
                Thread.Sleep(100);
            }
            WriteTime();
            Console.WriteLine("History Saved.");
        }

        // *** Save the entire camera buffer, and schedule continuing saves as we take more pictures
        static void SaveHistory(object sender, IvyMessageEventArgs e)
        {
            lock (LockObj)
            {
                if (SaveTimer == 0)
                {
                    WriteTime();
                    Console.WriteLine("Saving history");

                    Array.Copy(Snapshots, History, HistorySize);

                    Thread Worker = new Thread(Program.SaveHistoryWorker);
                    Worker.Priority = ThreadPriority.Lowest;
                    Worker.IsBackground = true;
                    Worker.Start(History);
                }
                else
                {
                    WriteTime();
                    Console.WriteLine("Extending rolling save period");
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
                WriteTime();
                Console.WriteLine("Failed to save snapshot: no data");
                return Timestamp();
            }

            if (Snapshot.PictureData.Length < 5)
            {
                WriteTime();
                Console.WriteLine("Failed to save snapshot: truncated data");
                return Timestamp();
            }

            try
            {
                Image img = (Image)Converter.ConvertFrom(Snapshot.PictureData);
                Int64 _Timestamp = Timestamp(Snapshot.Timestamp);
                String Filename = string.Format(OutputFormat, _Timestamp / 1000, _Timestamp % 1000);
                img.Save(Filename);

            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR");
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.StackTrace);
            }
            return Timestamp();
        }

        // *** Pull in a picture from the camera stream
        static void TakePicture()
        {
            Snapshots[RollingPtr].PictureData = Camera.Get("Camera 1").GetVideoFrame();
            Snapshots[RollingPtr].Timestamp = DateTime.Now;
        }

        private struct Picture
        {
            public byte[] PictureData;
            public DateTime Timestamp;
        }

        public static void WriteTime()
        {
            Console.Write(DateTime.Now.ToString("HH:mm:ss "));
        }

        public static Int64 Timestamp()
        {
            return Timestamp(DateTime.Now);
        }

        public static Int64 Timestamp(DateTime dateTime)
        {
            return (Int64)(dateTime - new DateTime(1970, 1, 1).ToLocalTime()).TotalMilliseconds;
        }
    }
}
