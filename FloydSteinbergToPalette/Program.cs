﻿using System;
using System.Collections.Generic;
using System.Numerics;
using System.Resources;
using System.Xml.Serialization;
using NDesk.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

class Program
{
    class Palette 
    {
        private RgbaVector[] entries;

        public Palette(Image<RgbaVector> image)
        {
            image.ProcessPixelRows(accessor => 
            {
                List<RgbaVector> foundValues = new List<RgbaVector>();
                for (int i = 0; i < image.Size.Height; i++)
                {
                    Span<RgbaVector> pixelRow = accessor.GetRowSpan(i);
                    for (int j = 0; j < image.Size.Width; j++)
                    {
                        ref RgbaVector pixel = ref pixelRow[j];
                        if (!foundValues.Contains(pixel))
                        {
                            foundValues.Add(pixel);
                        }
                    }
                }
                entries = foundValues.ToArray();
            });
        }

        public RgbaVector FindClosestEntry(RgbaVector test)
        {
            Vector3 error = new Vector3(10, 10, 10);
            var result = new RgbaVector(0, 0, 0);
            foreach (var entry in entries)
            {
                Vector3 localError = new Vector3(test.R - entry.R, test.G - entry.G, test.B - entry.B);
                if (localError.Length() < error.Length())
                {
                    result = entry;
                    error = localError;
                }
            }
            return result;
        }
    }

    static void convertToLinear(Image<RgbaVector> image)
    {
        image.Mutate(x => x.ProcessPixelRowsAsVector4(row => 
        {
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = new Vector4(MathF.Pow(row[i].X, 2.2f), MathF.Pow(row[i].Y, 2.2f), MathF.Pow(row[i].Z, 2.2f), 1);
            }
        }));
    }

    static void convertToGamma(Image<RgbaVector> image)
    {
        image.Mutate(x => x.ProcessPixelRowsAsVector4(row => 
        {
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = new Vector4(MathF.Pow(row[i].X, 1.0f / 2.2f), MathF.Pow(row[i].Y, 1.0f / 2.2f), MathF.Pow(row[i].Z, 1.0f / 2.2f), 1);
            }
        }));
    }

    static void Main(string[] args)
    {
        string inputFilename = "";
        string outputFilename = "";
        string paletteFilename = "";
        float strength = .75f;
        bool formulaFloydSteinberg = true;
        bool formulaAtkinson = false;
        bool formulaInterlace = false;
        bool multipass = false;

        var p = new OptionSet() {
            { "i|input=", "Input filename.", v => inputFilename = v },
            { "o|output=", "Output filename.", v => outputFilename = v },
            { "p|palette=", "Palette filename.", v => paletteFilename = v },
            { "s|strength=", "Error damping factor (float, default 0,75).", v => strength = float.Parse(v) },
            { "m|multipass=", "Multipass (Goes through all methods in order: interlace, atkinson, floydSteinberg)", v => { if (v.ToLower().Equals("true")) { multipass = true; } } },
            { "f|formula=", "Dithering formula: floydsteinberg, atkinson, interlace (default floydsteinberg).", v => {
                if (v.ToLower().Equals("atkinson"))
                { 
                    formulaFloydSteinberg = false;
                    formulaAtkinson = true;
                    formulaInterlace = false;
                }
                else if (v.ToLower().Equals("interlace"))
                {
                    formulaFloydSteinberg = false;
                    formulaAtkinson = false;
                    formulaInterlace = true;
                }
            } }
        };

        try {
            p.Parse(args);
        }        
        catch (OptionException ex) {
            Console.WriteLine("Invalid command line options!");
            Console.WriteLine(ex.Message);
            Console.WriteLine("Try --help for more information.");
        }

        using Image<RgbaVector> imageReference = Image.Load<RgbaVector>(inputFilename);
        convertToLinear(imageReference);
        using Image<RgbaVector> image = Image.Load<RgbaVector>(inputFilename);
        convertToLinear(image);
        using Image<RgbaVector> paletteImage = Image.Load<RgbaVector>(paletteFilename);
        convertToLinear(paletteImage);
        Palette pal = new Palette(paletteImage);

        Image<RgbaVector> output = new Image<RgbaVector>(image.Size.Width, image.Size.Height);

        int passes = multipass ? 3 : 1;
        
        for (int i = 0; i < passes; ++i)
        {
            if (multipass)
            {
                switch (i)
                {
                    case 0:
                        formulaInterlace = true;
                        formulaAtkinson = false;
                        formulaFloydSteinberg = false;
                        break;
                    case 1:
                        formulaInterlace = false;
                        formulaAtkinson = true;
                        formulaFloydSteinberg = false;
                        break;
                    case 2:
                        formulaInterlace = false;
                        formulaAtkinson = false;
                        formulaFloydSteinberg = true;
                        break;
                }
            }
            for (int y = 0; y < image.Size.Height; ++y)
            {
                for (int x = 0; x < image.Size.Width; ++x)
                {
                    RgbaVector oldPixel = image[x, y];
                    RgbaVector newPixel = pal.FindClosestEntry(oldPixel);
                    Vector3 error = new Vector3(oldPixel.R - newPixel.R, oldPixel.G - newPixel.G, oldPixel.B - newPixel.B);
                    output[x, y] = newPixel;
                    error *= strength;
                    if (formulaFloydSteinberg)
                    {
                        if (x < image.Size.Width - 1) {
                            RgbaVector existing = image[x + 1, y];
                            image[x + 1, y] = new RgbaVector(existing.R + error.X * 7 / 16, existing.G + error.Y * 7 / 16, existing.B  + error.Z * 7 / 16, 1);
                        }
                        if (x > 0 && y < image.Size.Height - 1) {
                            RgbaVector existing = image[x - 1, y + 1];
                            image[x - 1, y + 1] = new RgbaVector(existing.R + error.X * 3 / 16, existing.G + error.Y * 3 / 16, existing.B  + error.Z * 3 / 16, 1);
                        }
                        if (y < image.Size.Height - 1) {
                            RgbaVector existing = image[x, y + 1];
                            image[x, y + 1] = new RgbaVector(existing.R + error.X * 5 / 16, existing.G + error.Y * 5 / 16, existing.B  + error.Z * 5 / 16, 1);
                        }
                        if (x < image.Size.Width - 1 && y < image.Size.Height - 1) {
                            RgbaVector existing = image[x + 1, y + 1];
                            image[x + 1, y + 1] = new RgbaVector(existing.R + error.X * 1 / 16, existing.G + error.Y * 1 / 16, existing.B  + error.Z * 1 / 16, 1);
                        }
                    }
                    else if (formulaAtkinson)
                    {
                        if (x < image.Size.Width - 1) {
                            RgbaVector existing = image[x + 1, y];
                            image[x + 1, y] = new RgbaVector(existing.R + error.X * 0.125f, existing.G + error.Y * 0.125f, existing.B  + error.Z * 0.125f, 1);
                        }
                        if (x < image.Size.Width - 2) {
                            RgbaVector existing = image[x + 2, y];
                            image[x + 2, y] = new RgbaVector(existing.R + error.X * 0.125f, existing.G + error.Y * 0.125f, existing.B  + error.Z * 0.125f, 1);
                        }
                        if (x > 0 && y < image.Size.Height - 1) {
                            RgbaVector existing = image[x - 1, y + 1];
                            image[x - 1, y + 1] = new RgbaVector(existing.R + error.X * 0.125f, existing.G + error.Y * 0.125f, existing.B  + error.Z * 0.125f, 1);
                        }
                        if (y < image.Size.Height - 1) {
                            RgbaVector existing = image[x, y + 1];
                            image[x, y + 1] = new RgbaVector(existing.R + error.X * 0.125f, existing.G + error.Y * 0.125f, existing.B  + error.Z * 0.125f, 1);
                        }
                        if (x < image.Size.Width - 1 && y < image.Size.Height - 1) {
                            RgbaVector existing = image[x + 1, y + 1];
                            image[x + 1, y] = new RgbaVector(existing.R + error.X * 0.125f, existing.G + error.Y * 0.125f, existing.B  + error.Z * 0.125f, 1);
                        }
                        if (y < image.Size.Height - 2) {
                            RgbaVector existing = image[x, y + 2];
                            image[x, y + 2] = new RgbaVector(existing.R + error.X * 0.125f, existing.G + error.Y * 0.125f, existing.B  + error.Z * 0.125f, 1);
                        }
                    }
                    else if (formulaInterlace)
                    {
                        if (y % 2 == 1) { continue; }
                        else 
                        {
                            if (y < image.Size.Height - 1) {
                                RgbaVector existing = image[x, y + 1];
                                image[x, y + 1] = new RgbaVector(existing.R + error.X * 0.5f, existing.G + error.Y * 0.5f, existing.B  + error.Z * 0.5f, 1);
                            }
                        }
                    }
                }
            }
        }

        convertToGamma(output);
        output.Save(outputFilename);
    }
}
