using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using gg_downloader.Interfaces;
using gg_downloader.Services;
using gg_downloader.Models;
#if SFV
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
#endif

namespace gg_downloader
{
    internal class Program
    {
        private const string GOG_GAMES_WEB_ROOT = "https://gog-games.com";
        private const string GOG_GAMES_STORE_ROOT = "https://store.gog-games.com";
        private const string GOG_GAMES_CDN_ROOT = "https://cdn.gog-games.com";
        private static ISettingsProvider _settings;

        static async Task<int> Main(string[] args)
        {

            // Initialize the settings Provider
            _settings = new INISettingsProvider(GOG_GAMES_CDN_ROOT);

            // For Windows 7, we specifically need to tell the ServicePointManager to use 
            // TLS1.2. As we can only be running the .NET Framework version on Windows 7,
            // we'll conditionally compile this out by checking for .NET 5.0. OS Version 6.1
            // equates to Windows 7 _or_ Windows Server 2008, and hey, this fix is probably
            // needed for it too.
#if !NET
            if (System.Environment.OSVersion.Version.Major == 6 &&
                System.Environment.OSVersion.Version.Minor == 1)
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            }
#endif

            RootCommand rootCommand = new RootCommand("Download files from GOG Games CDN.");

            Command donateCommand = new Command("donate", "open the GOG Games Store link for CDN access");
            donateCommand.SetHandler(LaunchDonatePage);
            rootCommand.Add(donateCommand);

            Command userCommand = new Command("user", "set your GOG Games CDN username");
            userCommand.AddAlias("u");
            Argument<string> usernameArgument = new Argument<string>("username", "your GOG Games username");
            userCommand.AddArgument(usernameArgument);
            userCommand.SetHandler(SetUsername, usernameArgument);
            rootCommand.Add(userCommand);

            Command passwordCommand = new Command("password", "set your GOG Games CDN password");
            passwordCommand.AddAlias("p");
            Argument<string> passwordArgument = new Argument<string>("password", "your GOG Games password");
            passwordCommand.AddArgument(passwordArgument);
            passwordCommand.SetHandler(SetPassword, passwordArgument);
            rootCommand.Add(passwordCommand);

            Command resetCommand = new Command("reset", "reset username/password config");
            resetCommand.SetHandler(ResetAuth);
            rootCommand.Add(resetCommand);

            Command goggameAddressCommand = new Command("webroot", "set the GOG Games CDN root address");
            goggameAddressCommand.AddAlias("w");
            Argument<string> cdnRootArgument = new Argument<string>("url", "root GOG Games CDN URL");
            goggameAddressCommand.AddArgument(cdnRootArgument);
            goggameAddressCommand.SetHandler(SetCDNRoot, cdnRootArgument);
            rootCommand.Add(goggameAddressCommand);

            Command authCommand = new Command("auth", "check authentication and exit");
            authCommand.AddAlias("a");
            authCommand.SetHandler(CheckAuth);
            rootCommand.Add(authCommand);

            Command latestGamesCommand = new Command("latest", "list added/updated on GOG Games CDN");
            latestGamesCommand.AddAlias("l");
            latestGamesCommand.SetHandler(GetLatestGames);
            rootCommand.Add(latestGamesCommand);

            Command latestSlugsCommand = new Command("Latest", "list slugs of added/updated on GOG Games CDN");
            latestSlugsCommand.AddAlias("L");
            latestSlugsCommand.SetHandler(GetLatestSlugs);
            rootCommand.Add(latestSlugsCommand);

#if SFV
            // using local sfv database not currently supported, so the code is stubbed out for now.
            Command updateSFVCommand = new Command("sfv", "update offline SFV database");
            updateSFVCommand.AddAlias("s");
            updateSFVCommand.SetHandler(UpdateSFV);
            rootCommand.Add(updateSFVCommand);
#endif

            Option<string> userOption = new Option<string>("--username", "override your set GOG Games CDN username");
            userOption.AddAlias("-u");
            Option<string> passwordOption = new Option<string>("--password", "override your set GOG Games CDN password");
            passwordOption.AddAlias("-p");
            Option<string> rootOption = new Option<string>("-w", "override the default GOG Games CDN root");
            Option<bool> noDirOption = new Option<bool>("-n", () => false, "do not put files in a sub-directory");
            noDirOption.AddAlias("--no-dir");
            Option<bool> unsafeOption = new Option<bool>("--unsafe", () => false, "do not verify integrity of downloads");
            Option<bool> goodiesOption = new Option<bool>("--goodies", () => true, "download the goodies");
            Option<bool> patchesOption = new Option<bool>("--patches", () => false, "download the patches");
            Option<bool> gameOption = new Option<bool>("--game", () => true, "download the game files");
            Command downloadCommand = new Command("download", "download a game from the GOG Games CDN");
            downloadCommand.AddOption(userOption);
            downloadCommand.AddAlias("d");
            downloadCommand.AddOption(passwordOption);
            downloadCommand.AddOption(rootOption);
            downloadCommand.AddOption(noDirOption);
            downloadCommand.AddOption(unsafeOption);
            downloadCommand.AddOption(goodiesOption);
            downloadCommand.AddOption(patchesOption);
            downloadCommand.AddOption(gameOption);
            Argument<string> urlOrSlug = new Argument<string>("slug-or-url", "slug or URL to download");
            downloadCommand.AddArgument(urlOrSlug);
          
            downloadCommand.SetHandler(async (context) =>
            {
                await DownloadGame(
                    context.ParseResult.GetValueForArgument(urlOrSlug),
                    context.ParseResult.GetValueForOption(userOption) ?? _settings.UserName,
                    context.ParseResult.GetValueForOption(passwordOption) ?? _settings.Password,
                    context.ParseResult.GetValueForOption(rootOption) ?? _settings.CDNRoot,
                    context.ParseResult.GetValueForOption(noDirOption),
                    context.ParseResult.GetValueForOption(unsafeOption),
                    context.ParseResult.GetValueForOption(goodiesOption),
                    context.ParseResult.GetValueForOption(patchesOption),
                    context.ParseResult.GetValueForOption(gameOption)
                    );
            });

            rootCommand.AddCommand(downloadCommand);

            return await rootCommand.InvokeAsync(args);
        }

        private static async Task DownloadGame(string slugOrUrl, string username, string password,
            string downloadRoot, bool noDir, bool noVerify, bool goodies, bool patches, bool game)
        {

            // okay, here we go. First thing we need to sort out is whether we were given a URL 
            // or a slug. And in either case, whether it's valid.  We'll determine this by seeing if 
            // the slugOrUrl contains a protocol. If it is, we treat it as a URL and ignore the downloadRoot.
            // if it doesn't, we append the downloadRoot and build a URL. Once either of those routes have
            // completed, we can try pulling down that page, and see if it's a valid config.

            Uri uri = ConvertSlugOrUrlToUri(slugOrUrl);
            string slug = ConvertUriToSlug(uri);
            HttpResponseMessage page = await HttpRequest.Get(uri);
            if (!page.IsSuccessStatusCode)
            {
                Console.WriteLine($"GG-Downloader-Win: Error {(int)page.StatusCode} ({page.StatusCode}) retrieving {uri}");
                return;
            }

            // grab the SFV checksum file
            Dictionary<string, string> sfvDictionary = new Dictionary<string, string>();
            if (!noVerify)
            {
                string checkSums =
                    await (await HttpRequest.Get(new Uri(downloadRoot + "/sfv/" + slug + ".sfv"), username, password))
                        .Content.ReadAsStringAsync();
                foreach (string line in checkSums.Split(new [] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
                {
                    try
                    {
                        sfvDictionary.Add(line.Split(' ')[0], line.Split(' ')[2]);
                    }
                    catch (IndexOutOfRangeException)
                    {
                    }

                }
            }

            // grab the page content from the returned HttpResponseMessage
            string pageContent = await page.Content.ReadAsStringAsync();

            List<FileToDownloadInfo> files = new List<FileToDownloadInfo>();
            if (game) files.AddRange(ExtractGameFilesFromPage(pageContent));
            if (goodies) files.AddRange(ExtractGoodiesFromPage(pageContent));
            if (patches) files.AddRange(ExtractPatchesFromPage(pageContent));

            {
                int gamesCount = files.Count(f => f.Type == FileToDownloadInfo.FileType.Game);
                int goodiesCount = files.Count(f => f.Type == FileToDownloadInfo.FileType.Goody);
                int patchesCount = files.Count(f => f.Type == FileToDownloadInfo.FileType.Patch);
                Console.WriteLine($"Downloading {gamesCount} game files, {goodiesCount} goodies and {patchesCount} patches.");
            }

            if (!noDir)
            {
                Directory.CreateDirectory(slug);
            } 

            foreach (FileToDownloadInfo file in files)
            {
                string url = downloadRoot + "/" + 
                    (file.Type == FileToDownloadInfo.FileType.Patch ? "patches/" : "downloads/" + slug) + 
                    "/"+ file.FileName;
                string filename = (noDir ? "./" : slug + "/") + file.FileName;
                
                Console.WriteLine($"Downloading {url}");
                uint crc32CheckSum;

                using (var client = new HttpClientDownloadWithProgress(url, filename, username, password))
                {
                    client.ProgressChanged += (totalFileSize, totalBytesDownloaded, progressPercentage) => {
                        Console.Write($"\r{progressPercentage}% ({totalBytesDownloaded}/{totalFileSize})");
                    };

                    crc32CheckSum = await client.StartDownload();
                }
                Console.WriteLine($"\r{file.FileName} downloaded.");

                if (noVerify) continue;

                // check the file against the sfv dictionary.
                if (crc32CheckSum.ToString("X8").ToLower() != sfvDictionary[file.FileName])
                {
                    Console.WriteLine($"\r{file.FileName} failed SFV validation.");
                    Console.WriteLine("Halting...");
                    return;
                }
                else
                {
                    Console.WriteLine($"\r{file.FileName} passed SFV validation.");
                }
            }

        }

        private static string ConvertUriToSlug(Uri uri)
        {
            return uri.ToString().Substring(uri.ToString().LastIndexOf('/')+1);
        }

        private static List<string> ExtractFilesFromPage(Regex fileBlockRegex, string pageContent)
        {
            List<string> result = new List<string>();
            if (fileBlockRegex.IsMatch(pageContent))
            {
                string fileBlock = fileBlockRegex.Match(pageContent).Groups[1].Value;

                Regex fileNamesRegex = new Regex(@"class=""filename"">(\S+)</span>");
                foreach (Match m in fileNamesRegex.Matches(fileBlock))
                {
                    result.Add(m.Groups[1].Value);
                }
            }

            return result;
        }

        private static List<FileToDownloadInfo> ExtractGameFilesFromPage(string pageContent)
        {
            Regex fileBlockRegex = new Regex(@"Game Items Included<\/div>\s([\S\s]+?)<\/div>\s<\/div>");
            List<string> filesInBlock = ExtractFilesFromPage(fileBlockRegex, pageContent);
            List<FileToDownloadInfo> results = new List<FileToDownloadInfo>();
            foreach (string s in filesInBlock)
            {
                results.Add(new FileToDownloadInfo {  FileName = s, Type = FileToDownloadInfo.FileType.Game });
            }
            return results;
        }

        private static List<FileToDownloadInfo> ExtractGoodiesFromPage(string pageContent)
        {
            Regex goodiesBlockRegex = new Regex(@"Goodies Included<\/div>\s([\S\s]+?)<\/div>\s<\/div>");
            List<string> filesInBlock = ExtractFilesFromPage(goodiesBlockRegex, pageContent);
            List<FileToDownloadInfo> results = new List<FileToDownloadInfo>();
            foreach (string s in filesInBlock)
            {
                results.Add(new FileToDownloadInfo { FileName = s, Type = FileToDownloadInfo.FileType.Goody });
            }
            return results;
        }

        private static List<FileToDownloadInfo> ExtractPatchesFromPage(string pageContent)
        {
            Regex patchesBlockRegex = new Regex(@"Other Items Included<\/div>\s([\S\s]+?)<\/div>\s<\/div>");
            List<string> filesInBlock = ExtractFilesFromPage(patchesBlockRegex, pageContent);
            List<FileToDownloadInfo> results = new List<FileToDownloadInfo>();
            foreach (string s in filesInBlock)
            {
                results.Add(new FileToDownloadInfo { FileName = s, Type = FileToDownloadInfo.FileType.Patch });
            }
            return results;
        }

        private static Uri ConvertSlugOrUrlToUri(string slugOrUrl)
        {
            Uri url;
            if (Uri.IsWellFormedUriString(slugOrUrl, UriKind.Absolute))
            {
                _ = Uri.TryCreate(slugOrUrl, UriKind.Absolute, out url);
            }
            else
            {
                Uri.TryCreate($"{GOG_GAMES_WEB_ROOT}/game/{slugOrUrl}", UriKind.Absolute, out url);
            }
            Debug.Assert(url != null);
            return url;
        }

#if SFV
        private static async Task UpdateSFV()
        {
            HttpResponseMessage result = await HttpRequest.Get(
                new Uri(
                    "https://codeload.github.com/GOG-Games-com/GOG.com-Game-Collection-Verification/tar.gz/refs/heads/main",
                    UriKind.Absolute), string.Empty, string.Empty);

            Stream inStream = await result.Content.ReadAsStreamAsync();
            Stream gzipStream = new GZipInputStream(inStream);

            TarArchive tarArchive = TarArchive.CreateInputTarArchive(gzipStream, System.Text.Encoding.UTF8);
            tarArchive.ExtractContents(Path.GetTempPath());
            tarArchive.Close();

            gzipStream.Close();
            inStream.Close();

            File.Move(Path.GetTempPath() + "\\GOG.com-Game-Collection-Verification-main\\gog_collection.sfv", AppDomain.CurrentDomain.BaseDirectory + "\\gog_collection.sfv");
            Directory.Delete(Path.GetTempPath() + "\\GOG.com-Game-Collection-Verification-main\\");

            Console.WriteLine("GG-Downloader-Win: SFV Database updated");
        }
#endif

        private static async Task GetLatestSlugs()
        {
            HttpResponseMessage result =
                await HttpRequest.Get(new Uri($"{GOG_GAMES_WEB_ROOT}/rss"), string.Empty, string.Empty);
            string rssContent = await result.Content.ReadAsStringAsync();

            foreach (Match match in Regex.Matches(rssContent, @"<item>([\S\s]+?)<\/item>"))
            {
                string slug = Regex.Match(match.Value, @"<link>https:\/\/gog-games.com\/game\/([\S\s]+?)</link>").Groups[1].Value;
                Console.WriteLine(slug);
            }
        }

        private static async Task GetLatestGames()
        {
            HttpResponseMessage result =
                await HttpRequest.Get(new Uri($"{GOG_GAMES_WEB_ROOT}/rss"), string.Empty, string.Empty);
            string rssContent = await result.Content.ReadAsStringAsync();
            foreach (Match match in Regex.Matches(rssContent, @"<item>([\S\s]+?)<\/item>"))
            {
                string pubDate = Regex.Match(match.Value, @"<pubDate>([\S\s]+?)</pubDate>").Groups[1].Value;
                DateTime pubDateTime = DateTime.Parse(pubDate);
                string title = Regex.Match(match.Value, @"<title>([\S\s]+?)</title>").Groups[1].Value;
                title = title.Replace("&#039;", "'");
                title = title.Replace("&amp;", "&");
                string link = Regex.Match(match.Value, @"<link>([\S\s]+?)</link>").Groups[1].Value;

                string pubDateFormatted = pubDateTime.ToString("yyyy-MM-dd");
                Console.WriteLine($"{pubDateFormatted} \"{title}\" {link}");
            }
        }

        private static async Task CheckAuth()
        {
            if (string.IsNullOrWhiteSpace(_settings.UserName))
            {
                Console.Write("Username: ");
                string username = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(username))
                {
                    Console.WriteLine("GG-Downloader-Win: auth: No username provided");
                    return;
                }

                _settings.UserName = username.Trim();
            }

            if (string.IsNullOrWhiteSpace(_settings.Password))
            {
                Console.Write("Password: ");
                string password = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(password))
                {
                    Console.WriteLine("GG-Downloader-Win: auth: No password provided");
                    return;
                }

                _settings.Password = password.Trim();
            }

            HttpResponseMessage result = await HttpRequest.Get(new Uri($"{_settings.CDNRoot}/auth/authorize", UriKind.Absolute),
                _settings.UserName, _settings.Password);

            if (result.StatusCode == HttpStatusCode.Unauthorized)
            {
                Console.WriteLine("GG-Downloader-Win: auth: Incorrect username/password");
                return;
            }

            Console.WriteLine("GG-Downloader-Win: auth: Successful log-in");
        }

        private static void SetCDNRoot(string cdnRoot)
        {
            _settings.CDNRoot = cdnRoot;
            Console.WriteLine("GG-Downloader-Win: CDN Root set");
        }

        private static void ResetAuth()
        {
            Console.Write("Are you sure? (y/N) ");
            if (Console.ReadKey().KeyChar.ToString().ToUpper() != "Y") { return; }
            Console.WriteLine();

            _settings.UserName = string.Empty;
            _settings.Password = string.Empty;
            _settings.CDNRoot = GOG_GAMES_CDN_ROOT;
            Console.WriteLine("GG-Downloader-Win: Auth and CDN reset");
        }

        private static void SetPassword(string password)
        {
            _settings.Password = password.Trim();
            Console.WriteLine("GG-Downloader-Win: Password set");
        }

        private static void SetUsername(string username)
        {
            _settings.UserName = username.Trim();
            Console.WriteLine("GG-Downloader-Win: Username set");
        }

        internal static void LaunchDonatePage()
        {
            try
            {
                Process.Start(GOG_GAMES_STORE_ROOT);
            }
            catch
            {
                Console.WriteLine("Unable to launch a browser.\n{0}", GOG_GAMES_STORE_ROOT);
            }
        }

    }
}
