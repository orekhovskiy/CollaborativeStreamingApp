using Microsoft.MixedReality.WebRTC;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace CollaborativeStreamingApp
{
    public static class Scaler
    {
        private static unsafe void YUV2RGBManaged(byte[] pYUV, byte[] pRGB, uint width, uint height)
        {
            //returned pixel format is 2yuv - i.e. luminance, y, is represented for every pixel and the u and v are alternated
            //like this (where Cb = u , Cr = y)
            //Y0 Cb Y1 Cr Y2 Cb Y3 

            /*http://msdn.microsoft.com/en-us/library/ms893078.aspx
             * 
             C = 298 * (Y - 16) + 128
             D = U - 128
             E = V - 128
             R = clip(( C           + 409 * E) >> 8)
             G = clip(( C - 100 * D - 208 * E) >> 8)
             B = clip(( C + 516 * D          ) >> 8)

             * here are a whole bunch more formats for doing this...
             * http://stackoverflow.com/questions/3943779/converting-to-yuv-ycbcr-colour-space-many-versions
             */

            for (int r = 0; r < height; r++)
            {
                var pRGBOff = r * width * 3;
                var pYUVOff = r * width * 2;

                //process two pixels at a time
                for (int c = 0; c < width; c += 2)
                {
                    int C1 = 298 * (pYUV[pYUVOff + 1] - 16) + 128;
                    int C2 = 298 * (pYUV[pYUVOff + 3] - 16) + 128;
                    int D = pYUV[pYUVOff + 2] - 128;
                    int E = pYUV[pYUVOff + 0] - 128;

                    int R1 = (C1 + 409 * E) >> 8;
                    int G1 = (C1 - 100 * D - 208 * E) >> 8;
                    int B1 = (C1 + 516 * D) >> 8;

                    int R2 = (C2 + 409 * E) >> 8;
                    int G2 = (C2 - 100 * D - 208 * E) >> 8;
                    int B2 = (298 * C2 + 516 * D) >> 8;

                    //check for overflow
                    //unsurprisingly this takes the bulk of the time.
                    pRGB[pRGBOff + 0] = (byte)(R1 < 0 ? 0 : R1 > 255 ? 255 : R1);
                    pRGB[pRGBOff + 1] = (byte)(G1 < 0 ? 0 : G1 > 255 ? 255 : G1);
                    pRGB[pRGBOff + 2] = (byte)(B1 < 0 ? 0 : B1 > 255 ? 255 : B1);

                    pRGB[pRGBOff + 3] = (byte)(R2 < 0 ? 0 : R2 > 255 ? 255 : R2);
                    pRGB[pRGBOff + 4] = (byte)(G2 < 0 ? 0 : G2 > 255 ? 255 : G2);
                    pRGB[pRGBOff + 5] = (byte)(B2 < 0 ? 0 : B2 > 255 ? 255 : B2);
                    
                    pRGBOff += 6;
                    pYUVOff += 4;
                }
            }
        }
        static void encodeYUV420SP(byte[] yuv420sp, byte[] argb, uint width, uint height)
        {
            var frameSize = width * height;

            var yIndex = 0;
            var uvIndex = frameSize;

            int R, G, B, Y, U, V;
            int index = 0;
            for (int j = 0; j < height; j++)
            {
                for (int i = 0; i < width; i++)
                {

                    // a = (argb[index] & 0xff000000) >> 24; // a is not used obviously
                    R = argb[index * 3];
                    G = argb[index * 3 + 1];
                    B = argb[index * 3 + 2];

                    // well known RGB to YUV algorithm
                    Y = ((66 * R + 129 * G + 25 * B + 128) >> 8) + 16;
                    U = ((-38 * R - 74 * G + 112 * B + 128) >> 8) + 128;
                    V = ((112 * R - 94 * G - 18 * B + 128) >> 8) + 128;

                    // NV21 has a plane of Y and interleaved planes of VU each sampled by a factor of 2
                    //    meaning for every 4 Y pixels there are 1 V and 1 U.  Note the sampling is every other
                    //    pixel AND every other scanline.
                    yuv420sp[yIndex++] = (byte)((Y < 0) ? 0 : ((Y > 255) ? 255 : Y));
                    if (j % 2 == 0 && index % 2 == 0)
                    {
                        yuv420sp[uvIndex++] = (byte)((V < 0) ? 0 : ((V > 255) ? 255 : V));
                        yuv420sp[uvIndex++] = (byte)((U < 0) ? 0 : ((U > 255) ? 255 : U));
                    }

                    index++;
                }
            }
        }

        public static I420AVideoFrame GetResizeFrame(I420AVideoFrame frame, int desiredWidth, int desiredHeight, PixelBitsDistribution pixelBitsDistribution)
        {
            int pixelSize = (int)frame.width * (int)frame.height;
            int byteSize = (pixelSize / 2 * 3); // I420 = 12 bits per pixel
            byte[] frameBytes = new byte[byteSize];
            frame.CopyTo(frameBytes);

            var rgbBytes = new byte[pixelSize * 3];

            YUV2RGBManaged(frameBytes, rgbBytes, frame.width, frame.height);

            encodeYUV420SP(frameBytes, rgbBytes, frame.width, frame.height);

            return new I420AVideoFrame()
            {
                
            };
        }

        public static I420AVideoFrame OldGetResizeFrame(I420AVideoFrame frame, int desiredWidth, int desiredHeight, PixelBitsDistribution pixelBitsDistribution)
        {
            int pixelSize = (int)frame.width * (int)frame.height;
            int byteSize = (pixelSize / 2 * 3); // I420 = 12 bits per pixel
            byte[] frameBytes = new byte[byteSize];
            frame.CopyTo(frameBytes);

            var dataYBits = TakeUnitBits(frameBytes, 12, 0, 8);
            var dataUBits = TakeUnitBits(frameBytes, 12, 8, 2);
            var dataVBits = TakeUnitBits(frameBytes, 12, 10, 2);

            var dataYBitsResized = ResizePixels(dataYBits, frame.width, frame.height, desiredWidth, desiredHeight, pixelBitsDistribution.YBits);
            var dataUBitsResized = ResizePixels(dataUBits, frame.width, frame.height, desiredWidth, desiredHeight, pixelBitsDistribution.UBits);
            var dataVBitsResized = ResizePixels(dataVBits, frame.width, frame.height, desiredWidth, desiredHeight, pixelBitsDistribution.VBits);

            var dataYBytesResized = ConvertToByteArray(dataYBitsResized);
            var dataUBytesResized = ConvertToByteArray(dataUBitsResized);
            var dataVBytesResized = ConvertToByteArray(dataVBitsResized);

            I420AVideoFrame resizedFrame = new I420AVideoFrame();
            resizedFrame.height = (uint)desiredHeight;
            resizedFrame.width = (uint)desiredWidth;
            resizedFrame.strideA = 0;
            resizedFrame.strideY = desiredWidth;
            resizedFrame.strideU = desiredHeight * 8 / 9;
            resizedFrame.strideV = desiredHeight;
            resizedFrame.dataA = Marshal.AllocHGlobal(dataYBytesResized.Length);
            resizedFrame.dataY = Marshal.AllocHGlobal(dataYBytesResized.Length);
            resizedFrame.dataU = Marshal.AllocHGlobal(dataUBytesResized.Length);
            resizedFrame.dataV = Marshal.AllocHGlobal(dataVBytesResized.Length);
            Marshal.Copy(dataYBytesResized, 0, resizedFrame.dataY, dataYBytesResized.Length);
            Marshal.Copy(dataUBytesResized, 0, resizedFrame.dataU, dataUBytesResized.Length);
            Marshal.Copy(dataVBytesResized, 0, resizedFrame.dataV, dataVBytesResized.Length);
            return resizedFrame;
        }

        private static BitArray TakeUnitBits(byte[] bytes, int totalBitsPerPixel, int bitsOffset, int unitBits)
        {
            var bits = new BitArray(bytes);
            var bitsToSkip = bits.Length / totalBitsPerPixel * bitsOffset;
            var bitsToTake = bits.Length / totalBitsPerPixel * unitBits;
            var totalUnitBits = SliceBitArray(bits, bitsToSkip, bitsToTake);
            return totalUnitBits;
        }

        private static BitArray ResizePixels(BitArray bits, uint w1, uint h1, int w2, int h2, int unitSize)
        {
            BitArray temp = new BitArray(w2 * h2);
            double x_ratio = w1 / (double)w2;
            double y_ratio = h1 / (double)h2;
            double px, py;
            for (int i = 0; i < h2; i += unitSize)
            {
                for (int j = 0; j < w2; j += unitSize)
                {
                    px = Math.Floor(j * x_ratio);
                    py = Math.Floor(i * y_ratio);
                    for (int k = 0; k < unitSize; k++)
                    {
                        temp[(i * w2) + j + k] = bits[(int)((py * w1) + px) + k];
                    }
                }
            }
            return temp;
        }
        private static BitArray SliceBitArray(BitArray array, int skip, int take)
        {
            var temp = new bool[take];
            for (var i = 0; i < take; i++)
            {
                temp[i] = array[skip + i];
            }
            return new BitArray(temp);
        }

        private static byte[] ConvertToByteArray(BitArray bits)
        {
            if (bits.Length % 8 != 0) throw new Exception("Unable to cast");
            byte[] bytes = new byte[bits.Length / 8];
            bits.CopyTo(bytes, 0);
            return bytes;
        }
    }
}
