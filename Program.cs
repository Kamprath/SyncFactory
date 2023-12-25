using System.Diagnostics;
using Newtonsoft.Json;
using Renci.SshNet;
using System.Text;
using System.Globalization;
using Renci.SshNet.Sftp;

class Program
{
    const string VERSION = "1.0.2";
    const string APP_ID = "526870";
    const string GAME_PROCESS_NAME = "FactoryGame";

    static string configPath
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "SyncFactory", "config.json");
        }
    }

    static string saveGamesPath
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "Saved", "SaveGames");
        }
    }

    static string privateKeyPath
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "SyncFactory", "key");
        }
    }

    static void Main()
    {
        Console.WriteLine($"SyncFactory v{VERSION}\n");

        Config? config = GetConfig();

        if (config == null)
        {
            config = SetUpConfig(SetUpAuth());
        }

        // run setup again if save file was deleted
        if (!File.Exists(GetLatestAutosavePath(config.SaveName)))
        {
            if (SetUpConfig(config) == null)
            {
                Console.WriteLine("ERROR: Failed to set up config.");
                return;
            }
        }

        try
        {
            using (var sftp = new SftpClient(GetConnectionInfo(config)))
            {
                Download(sftp, config);

                var startingLastModified = File.GetLastWriteTime(GetLatestAutosavePath(config.SaveName));

                RunGame();

                var endingLastModified = File.GetLastWriteTime(GetLatestAutosavePath(config.SaveName));

                if (endingLastModified > startingLastModified)
                {
                    Upload(sftp, config);
                } else
                {
                    Console.WriteLine($"\nSave file hasn't changed. Skipping upload.");
                }
            }

            Thread.Sleep(2000);
        } catch (Exception e)
        {
            Console.WriteLine("ERROR:\n" + e.Message);

            Console.WriteLine("\nPress any key to exit...");
            Console.Read();
        }
    }

    static Config SetUpAuth()
    {
        Config config = new Config();

        Console.WriteLine("Welcome to SyncFactory! This program will help you set up your Satisfactory save file for syncing.\n");

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
        File.WriteAllText(privateKeyPath, input.ToString());

        return config;
    }

    static Config? GetConfig()
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<Config>(File.ReadAllText(configPath));
        } catch (Exception e)
        {
            Console.WriteLine("ERROR: Invalid config file.");
            return null;
        }
    }

    static Config? SetUpConfig(Config? existingConfig = null)
    {
        Config config = existingConfig ?? new Config
        {
            Host = "",
            Username = "",
            SaveName = ""
        };

        string firstDirectory = Directory.GetDirectories(saveGamesPath).FirstOrDefault();

        if (firstDirectory == null)
        {
            Console.WriteLine("Error: No save game folder found. Make sure Satisfactory is installed.");
            Environment.Exit(0);
        }

        // create config directory
        var configDirectory = Path.GetDirectoryName(configPath);
        if (!Directory.Exists(Path.GetDirectoryName(configPath)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
        }

        using (var sftp = new SftpClient(GetConnectionInfo(config)))
        {
            Connect(config.Username, config.Host, sftp);

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
                    Console.WriteLine($" {i++}. {save.Name}");
                }

                Console.WriteLine("--------------------");

                Console.Write("\nEnter save number to download: ");
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

                var latestSftpFile = GetLatestSftpSave(sftp, selectedSave.Name);
                DownloadSaveFile(sftp, selectedSave.Name, latestSftpFile.FullName, saveFilePath);

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

                for (int i = 0; i < saveNames.Length; i++)
                {
                    // remove the rest of the filename starting at _autosave* and print the name of the save
                    Console.WriteLine($"{i + 1}. {saveNames[i]}");
                }

                Console.WriteLine("Enter number of save to upload:");
                int selectedSaveNameIndex = int.Parse(Console.ReadLine());

                string selectedSaveName = saveNames[selectedSaveNameIndex - 1];
                string saveFilePath = Path.Combine(firstDirectory, GetLatestAutosavePath(selectedSaveName));

                 //Store the path to the uploaded .sav file in the config
                config.SaveName = selectedSaveName;

                Upload(sftp, config);
            }

            sftp.Disconnect();
        }

        return SaveConfig(config);
    }

    static Config? SaveConfig(Config config)
    {
        try
        {
            string configJson = JsonConvert.SerializeObject(config);
            File.WriteAllText(configPath, configJson);
        } catch (Exception e)
        {
            Console.WriteLine($"ERROR: Failed to write to config file - {e.Message}\n");
            return null;
        }

        return config;
    }

    static void DownloadSaveFile(SftpClient sftp, string saveName, string remotePath, string localPath)
    {
        Console.Write($"\nDownloading latest version of {saveName}...");

        var fileStream = File.OpenWrite(localPath);
        sftp.DownloadFile(remotePath, fileStream);
        fileStream.Close();

        Console.Write("Done\n");
        Console.WriteLine($"Updated {localPath}");
    }

    static ISftpFile? GetLatestSftpSave(SftpClient sftp, string saveName)
    {
        var saveFiles = sftp.ListDirectory($"/saves/{saveName}").Where(file => file.Name != "." && file.Name != "..").ToList();

        if (saveFiles.Count == 0)
        {
            return null;
        }

        return saveFiles.OrderByDescending(f => f.Name).First();
    }

    static string? GetLatestAutosavePath(string saveName)
    {
        string saveGamesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "Saved", "SaveGames");
        string saveDirectory = Directory.GetDirectories(saveGamesDirectory).FirstOrDefault();

        // get list of files in the saveDirectory starting with saveName
        var saveFiles = Directory.GetFiles(saveDirectory, saveName + "*_autosave_*.sav");

        if (saveFiles.Length == 0)
        {
            return null;
        }

        // get the one with the latest last write time
        var latestFile = saveFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();

        return latestFile;
    }

    static bool Connect(string username, string host, SftpClient sftp)
    {
        if (sftp.IsConnected)
        {
            return true;
        }

        Console.Write($"Connecting to {username}@{host}...");

        try
        {
            sftp.Connect();
            Console.Write(" Connected\n");
            return true;
        } catch (Exception e)
        {
            Console.Write($" Failed: {e.Message}");
            return false;
        }
    }
    static void Download(SftpClient sftp, Config config)
    {
        Connect(config.Username, config.Host, sftp);

        var saveFilePath = GetLatestAutosavePath(config.SaveName);
        var localWriteTime = File.GetLastWriteTime(saveFilePath);

        if (!sftp.Exists($"/saves/{config.SaveName}"))
        {
            Console.WriteLine($"No save files found on server for {config.SaveName}");
            return;
        }

        var latestFile = GetLatestSftpSave(sftp, config.SaveName);

        if (latestFile != null)
        {
            DateTime remoteWriteTime = DateTime.ParseExact(latestFile.Name.Replace(".sav", ""), "yyMMdd-HHmmss", CultureInfo.InvariantCulture);
            
            if (remoteWriteTime > localWriteTime)
            {
                DownloadSaveFile(sftp, config.SaveName, latestFile.FullName, saveFilePath);
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
        Console.Write("\nStarting Satisfactory...");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = $"/c start steam://rungameid/{APP_ID}",
                CreateNoWindow = true,
                UseShellExecute = false
            });

            // Wait for the game process to start
            Process gameProcess = null;
            while (gameProcess == null)
            {
                gameProcess = Process.GetProcessesByName(GAME_PROCESS_NAME).FirstOrDefault();
                Thread.Sleep(1000);
            }

            Console.Write(" Started\n");

            // Wait for the game to exit
            gameProcess.WaitForExit();

            Console.WriteLine("Game exited.");
        }
        catch (Exception ex)
        {
            Console.Write($" Failed. \nError: {ex.Message}\n");
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }

    static void Upload(SftpClient sftp, Config config)
    {
        string saveName = config.SaveName;
        string savePath = GetLatestAutosavePath(saveName);
        string fileName = Path.GetFileName(savePath);

        // get timestamp of file last modified time
        DateTime localWriteTime = File.GetLastWriteTime(savePath);
        string timestring = localWriteTime.ToString("yyMMdd-HHmmss");

        Connect(config.Username, config.Host, sftp);

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
            Console.WriteLine("ERROR: No private key file found at " + keyPath);
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
