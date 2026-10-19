using System.Text.Json;
using System.Text.Json.Serialization;
using BandKey.Windows;

namespace BandKey.SessionLab;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json=new(){WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,Converters={new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)}};
    [STAThread]
    private static void Main(string[] args)
    {
        string? Arg(string name){int i=Array.IndexOf(args,name);return i>=0 && i+1<args.Length?args[i+1]:null;}
        string directory=Arg("--data")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BandKeySessionLab");
        string? output=Arg("--output");
        bool guestChecked=false,enteredLab=false;
        void PolicyReport(bool allowed,string? error=null,bool? lockRequested=false)
        {
            if(output==null)return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output,JsonSerializer.Serialize(new{guestAllowed=allowed,error,loginConfigurationModified=false,windowsLockRequested=lockRequested,windowsAuthenticationSubmitted=false,credentialsAccessed=false},Json));
        }
        try
        {
            ApplicationConfiguration.Initialize();
            if(args.Contains("--render"))
            {
                if(output==null)throw new ArgumentException("Render output required");
                using var preview=new SessionLabForm(directory,true){Opacity=0,ShowInTaskbar=false};preview.Show();Application.DoEvents();preview.PerformLayout();
                using var bitmap=new Bitmap(preview.ClientSize.Width,preview.ClientSize.Height);preview.Controls[0].DrawToBitmap(bitmap,new Rectangle(Point.Empty,preview.ClientSize));
                bitmap.Save(output);preview.Close();return;
            }
            VmNativePolicy.RequireGuest();
            guestChecked=true;
            if(args.Contains("--check-guest")){PolicyReport(true);return;}
            enteredLab=true;Application.Run(new SessionLabForm(directory,false));
        }
        catch(Exception error)
        {
            Environment.ExitCode=2;
            try{PolicyReport(guestChecked,error.GetType().Name,enteredLab?null:false);}catch(Exception){/* Never open a crash/authentication dialog. */}
        }
    }
}
