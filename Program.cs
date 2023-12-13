using System.Diagnostics;
using Newtonsoft.Json;
using Renci.SshNet;
using System.Text;
using System.Globalization;

class Program
{
    static void Main()
    {
        string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "SyncFactory", "config.json");
        Config config;

        if (!File.Exists(configPath))
        {
            config = SetupProcess(configPath);
        }
        else
        {
            config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(configPath));
        }

        // if the save file doesn't exist, call SetupProcess()
        if (!File.Exists(GetLatestSave(config.SaveName)))
        {
            SetupProcess(configPath);
        }

        try
        {
            using (var sftp = new SftpClient(GetConnectionInfo(config)))
            {
                DownloadProcess(sftp, config);
                var startingLastModified = File.GetLastWriteTime(GetLatestSave(config.SaveName));

                RunGame();

                var endingLastModified = File.GetLastWriteTime(GetLatestSave(config.SaveName));

                if (endingLastModified != startingLastModified)
                {
                    UploadProcess(sftp, config);
                } else
                {
                    Console.WriteLine($"\nSave file hasn't changed. Skipping upload.");
                }
            }
        } catch (Exception e)
        {
            Console.WriteLine("ERROR:\n" + e.Message);

            Console.WriteLine("\nPress any key to exit...");
            Console.Read();
        }
    }

      static Config SetupProcess(string configPath)
    {
        Config config = new Config
        {
            Host = "",
            Username = "",
            SaveName = ""
        };

        string saveGamesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "Saved", "SaveGames");
        string firstDirectory = Directory.GetDirectories(saveGamesDirectory).FirstOrDefault();


        if (firstDirectory == null)
        {
            Console.WriteLine("Error: No save game folder found. Make sure Satisfactory is installed.");
            Environment.Exit(0);
        }

        Console.WriteLine("Welcome to SyncFactory! This program will help you set up your Satisfactory save file for syncing.\n");

        // create config directory
        var configDirectory = Path.GetDirectoryName(configPath);
        if (!Directory.Exists(Path.GetDirectoryName(configPath)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
        }

        Console.Write("Enter SFTP Host: ");
        config.Host = Console.ReadLine();

        Console.Write("Enter SFTP Username: ");
        config.Username = Console.ReadLine();

        Console.WriteLine($"Paste SSH Private Key for {config.Username}@{config.Host}: \n");
        StringBuilder input = new StringBuilder();
        string line;
        while ((line = Console.ReadLine()) != "")
        {
            input.AppendLine(line);
        }

        // write value of key to %AppData%\FactoryGame\SyncFactory\key
        File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "SyncFactory", "key"), input.ToString());

        using (var sftp = new SftpClient(GetConnectionInfo(config)))
        {
            Connect(config, sftp);

            Console.WriteLine("\nDo you want to download an existing world file or upload your own? (Enter 'download' or 'upload'):");
            string choice = Console.ReadLine();

            if (choice == "download")
            {
                Console.WriteLine("\nSAVES:");
                Console.WriteLine("--------------------");

                 //List all save files on the server and prompt the user for their choice
                var saves = sftp.ListDirectory("/saves").Where(file => file.Name != "." && file.Name != "..").ToList();

                var i = 1;
                foreach (var save in saves)
                {
                    Console.WriteLine($" {i}. {save.Name}");
                }

                Console.WriteLine("--------------------");

                Console.Write("\nEnter save number to download:");
                int selectedFileIndex = int.Parse(Console.ReadLine());

                 //Download the selected save file to the first directory in the local save games folder
                var selectedSave = saves.ElementAt(selectedFileIndex - 1);
                string saveFilePath = Path.Combine(firstDirectory, $"{selectedSave.Name}_autosave_0.sav");

                // Find files that begin with the selectedFolder name (delimited by _autosave*)
                string[] existingSaveFiles = Directory.GetFiles(firstDirectory, $"{selectedSave.Name}_autosave*.sav");

                if (existingSaveFiles.Length > 0)
                {
                    Console.WriteLine("\nA save file with that name already exists. Do you want to overwrite it? (Enter 'yes' or 'no'):");
                    string overwriteChoice = Console.ReadLine();

                    if (overwriteChoice == "yes")
                    {
                        // delete each file in existingSaveFiles
                        foreach (string file in existingSaveFiles)
                        {
                            File.Delete(file);
                        }
                    }
                    else
                    {
                        Console.WriteLine("Aborting download.");
                        sftp.Disconnect();
                        return config;
                    }
                }

                // download most recent file from /saves/{saveName}
                var saveFiles = sftp.ListDirectory($"/saves/{selectedSave.Name}").Where(file => file.Name != "." && file.Name != "..").ToList();
                var latestFileName = saveFiles.OrderByDescending(f => f.Name).First().Name;

                Console.Write($"\nDownloading {selectedSave.Name} to {saveFilePath}...");
                
                sftp.DownloadFile($"/saves/{selectedSave.Name}/{latestFileName}", File.OpenWrite(saveFilePath));

                Console.Write("Done!\n");

                config.SaveName = selectedSave.Name;
            }
            else
            {
                Console.WriteLine("List of local saves:");
                string[] savFiles = Directory.GetFiles(firstDirectory, "*_autosave_*.sav");

                if (savFiles.Length == 0)
                {
                    Console.WriteLine("No save files found.");
                    Environment.Exit(0);
                }
                // remove file path and "_autosave*" 
                string[] saveNames = savFiles.Select(f =>
                {
                    int index = f.IndexOf("_autosave");
                    return index >= 0 ? f.Substring(0, index) : f;
                }).Distinct().Select(f => Path.GetFileName(f)).ToArray();

                //savFiles = savFiles.GroupBy(f => f.Substring(0, f.IndexOf("_autosave"))).Select(g => g.First()).ToArray();
                //string[] saveNames = savFiles.Select(f => f.Substring(0, f.IndexOf("_autosave"))).Distinct().ToArray();

                for (int i = 0; i < saveNames.Length; i++)
                {
                    // remove the rest of the filename starting at _autosave* and print the name of the save
                    Console.WriteLine($"{i + 1}. {saveNames[i]}");
                }

                Console.WriteLine("Enter the number of the save you want to upload:");
                int selectedSaveNameIndex = int.Parse(Console.ReadLine());

                string selectedSaveName = saveNames[selectedSaveNameIndex - 1];
                string saveFilePath = Path.Combine(firstDirectory, GetLatestSave(selectedSaveName));

                 //Store the path to the uploaded .sav file in the config
                config.SaveName = selectedSaveName;

                UploadProcess(sftp, config);
            }

            sftp.Disconnect();
        }

        // Write the config to the config file
        string configJson = JsonConvert.SerializeObject(config);
        File.WriteAllText(configPath, configJson);

        return config;
    }

    static string? GetLatestSave(string saveName)
    {
        string saveGamesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "Saved", "SaveGames");
        string saveDirectory = Directory.GetDirectories(saveGamesDirectory).FirstOrDefault();

        // get list of files in the saveDirectory starting with saveName
        var saveFiles = Directory.GetFiles(saveDirectory, saveName + "*_autosave_*.sav");

        // if no files, return null
        if (saveFiles.Length == 0)
        {
            return null;
        }

        // get the one with the latest last write time
        var latestFile = saveFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();

        // return the path to that file
        return latestFile;
    }

    static bool Connect(Config config, SftpClient sftp)
    {
        Console.Write($"Connecting to {config.Username}@{config.Host}...");

        try
        {
            sftp.Connect();
            Console.Write(" Connected!\n");
            return true;
        } catch (Exception e)
        {
            Console.Write($" Failed:\n {e.Message}");
            return false;
        }
    }
    static void DownloadProcess(SftpClient sftp, Config config)
    {
        Connect(config, sftp);

        var saveFilePath = GetLatestSave(config.SaveName);
        var saveName = Path.GetFileNameWithoutExtension(saveFilePath);
        var saveFileName = Path.GetFileName(saveFilePath);
        var localWriteTime = File.GetLastWriteTime(saveFilePath);

        if (!sftp.Exists($"/saves/{config.SaveName}"))
        {
            Console.WriteLine($"No save files found on server for {config.SaveName}");
            return;
        }

        var sftpFiles = sftp.ListDirectory($"/saves/{config.SaveName}").ToList();
        if (sftpFiles.Count > 0)
        {
            var remoteFile = sftpFiles.OrderByDescending(f => f.Name).First();
            
            DateTime remoteWriteTime = DateTime.ParseExact(remoteFile.Name.Replace(".sav", ""), "yyMMdd-HHmmss", CultureInfo.InvariantCulture);
            
            if (remoteWriteTime > localWriteTime)
            {
                string backupFilePath = saveFilePath + ".backup";
                File.Move(saveFilePath, backupFilePath);

                var saveFileOutput = File.OpenWrite(saveFilePath);
                sftp.DownloadFile(remoteFile.FullName, saveFileOutput);

                var localTime = remoteWriteTime.ToLocalTime();
                Console.WriteLine($"\nDownloaded latest version of {config.SaveName} (Last updated {localTime.Month}/{localTime.Day}/{localTime.Year} {localTime.Hour}:{localTime.Minute})");
            } else
            {
                Console.WriteLine($"\nYou have the most recent version of {config.SaveName}.");
            }
        } else
        {
            Console.WriteLine($"No save files found on server for {config.SaveName}");
        }

        sftp.Disconnect();
    }

    static void RunGame()
    {
        var appId = "526870";
        var gameProcessName = "FactoryGame";

        Console.Write("\nStarting Satisfactory...");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = $"/c start steam://rungameid/{appId}",
                CreateNoWindow = true,
                UseShellExecute = false
            });

            // Wait for the game process to start
            Process gameProcess = null;
            while (gameProcess == null)
            {
                gameProcess = Process.GetProcessesByName(gameProcessName).FirstOrDefault();
                Thread.Sleep(1000); // Check every second
            }

            Console.Write(" Started\n\n");

            // Wait for the game to exit
            gameProcess.WaitForExit();
        }
        catch (Exception ex)
        {
            Console.Write($" Failed. \nError: {ex.Message}\n");
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }

    static void UploadProcess(SftpClient sftp, Config config)
    {
        string saveName = config.SaveName;
        string savePath = GetLatestSave(saveName);
        string fileName = Path.GetFileName(savePath);

        // get timestamp of file last modified time
        DateTime localWriteTime = File.GetLastWriteTime(savePath);
        string timestring = localWriteTime.ToString("yyMMdd-HHmmss");

        Connect(config, sftp);

        if (!sftp.Exists($"/saves/{saveName}"))
        {
            sftp.CreateDirectory($"/saves/{saveName}");
        }

        var sftpFiles = sftp.ListDirectory($"/saves/{saveName}").Where(file => file.Name != "." && file.Name != "..").ToList();

        if (sftpFiles.Count > 0)
        {
            var remoteFile = sftpFiles.OrderByDescending(f => f.Name).First();

            DateTime remoteWriteTime = DateTime.ParseExact(remoteFile.Name.Replace(".sav", ""), "yyMMdd-HHmmss", CultureInfo.InvariantCulture);

            if (localWriteTime < remoteWriteTime)
            {
                Console.WriteLine($"Uh oh... You have an older version of {saveName}. Skipping upload.");
                string backupFilePath = savePath + ".backup";

                if (File.Exists(backupFilePath))
                {
                    Console.WriteLine("Deleted previous backup.");
                    File.Delete(backupFilePath);
                }

                File.Copy(savePath, backupFilePath);
                Console.WriteLine($"Your local version has been backed up at {backupFilePath}.");

                return;
            }
        }

        Console.Write($"\nUploading {saveName}... ");
        
        sftp.UploadFile(File.OpenRead(savePath), $"/saves/{saveName}/{timestring}.sav");
        sftp.Disconnect();

        Console.Write("Done");
    }

    static ConnectionInfo? GetConnectionInfo(Config config)
    {
        // read private key
        string keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "SyncFactory", "key");

        // check if key exists
        if (!File.Exists(keyPath))
        {
            Console.WriteLine("ERROR: No key file found at " + keyPath);
            return null;
        }

        var stream = new FileStream(keyPath.ToString(), FileMode.Open, FileAccess.Read);
        var file = new PrivateKeyFile(stream);
        var authMethod = new PrivateKeyAuthenticationMethod(config.Username, file);

        return new ConnectionInfo(config.Host, config.Username, authMethod);
    }
}

class Config
{
    public string Host { get; set; }
    public string Username { get; set; }
    public string SaveName { get; set; }
}
