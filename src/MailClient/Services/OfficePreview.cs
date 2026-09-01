using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Syncfusion.DocIORenderer;
using Syncfusion.Presentation;
using Syncfusion.PresentationRenderer;
using Syncfusion.XlsIO;
using Syncfusion.XlsIORenderer;

namespace MailClient.Services;

/// Converts Word / Excel / PowerPoint attachments to PDF (shown in the WebView2 preview) using
/// Syncfusion. Needs a Syncfusion Community licence key, taken from the SYNCFUSION_LICENSE_KEY
/// environment variable or a "syncfusion.license" file in the data directory. Without a key the
/// converters still run but stamp an evaluation banner on the output.
public static class OfficePreview
{
    private static readonly string[] Word = { ".doc", ".docx", ".rtf", ".odt" };
    private static readonly string[] Excel = { ".xls", ".xlsx", ".xlsm", ".ods", ".csv" };
    private static readonly string[] Ppt = { ".ppt", ".pptx", ".odp" };

    public static bool Licensed { get; private set; }

    public static bool CanConvert(string extension)
    {
        var ext = extension.ToLowerInvariant();
        return Word.Contains(ext) || Excel.Contains(ext) || Ppt.Contains(ext);
    }

    public static void RegisterLicense()
    {
        try
        {
            var key = Environment.GetEnvironmentVariable("SYNCFUSION_LICENSE_KEY");
            if (string.IsNullOrWhiteSpace(key))
            {
                var file = Path.Combine(AppPaths.DataDirectory, "syncfusion.license");
                if (File.Exists(file))
                {
                    key = File.ReadAllText(file).Trim();
                }
            }

            if (!string.IsNullOrWhiteSpace(key))
            {
                Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense(key);
                Licensed = true;
            }
            else
            {
                LoggingService.Info("OfficePreview",
                    "No Syncfusion licence key (SYNCFUSION_LICENSE_KEY env var or syncfusion.license file); "
                    + "Office previews will carry an evaluation banner.");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("OfficePreview.RegisterLicense", ex);
        }
    }

    /// Converts the given Office document bytes to a PDF, or returns null if the type isn't
    /// supported or conversion fails.
    public static Task<byte[]?> ToPdfAsync(byte[] data, string extension)
    {
        var ext = extension.ToLowerInvariant();
        return Task.Run<byte[]?>(() =>
        {
            try
            {
                using var input = new MemoryStream(data);
                using var output = new MemoryStream();

                if (Word.Contains(ext))
                {
                    using var doc = new WordDocument(input, Syncfusion.DocIO.FormatType.Automatic);
                    using var renderer = new DocIORenderer();
                    using var pdf = renderer.ConvertToPDF(doc);
                    pdf.Save(output);
                }
                else if (Excel.Contains(ext))
                {
                    using var engine = new ExcelEngine();
                    engine.Excel.DefaultVersion = ExcelVersion.Xlsx;
                    var workbook = engine.Excel.Workbooks.Open(input);
                    var renderer = new XlsIORenderer();
                    using var pdf = renderer.ConvertToPDF(workbook);
                    pdf.Save(output);
                }
                else if (Ppt.Contains(ext))
                {
                    using var presentation = Presentation.Open(input);
                    presentation.PresentationRenderer = new PresentationRenderer();
                    using var pdf = PresentationToPdfConverter.Convert(presentation);
                    pdf.Save(output);
                }
                else
                {
                    return null;
                }

                return output.ToArray();
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"OfficePreview.ToPdfAsync ({ext})", ex);
                return null;
            }
        });
    }
}
