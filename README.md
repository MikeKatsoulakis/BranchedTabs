# 🪄 Branched Tabs for Visual Studio

**Branched Tabs** is a Visual Studio extension that automatically saves and restores your open documents **per Git branch**. Seamlessly switch branches and get back exactly to where you left off!


## ✨ Features

- 🔀 Automatically save open tabs per Git branch
- 📂 Restore tabs when switching branches
- ⚙️ Configurable behavior:
  - **Restore only**
  - **Replace and keep unsaved**
  - **Replace all and save unsaved**
- ✅ Lightweight and works silently in the background
- 🔧 Optional: Disable or customize behavior via settings


## 🛠 Installation

1. Open the `.vsix` file from [Releases](https://github.com/MikeKatsoulakis/BranchedTabs/releases).

2. Restart Visual Studio.


## ⚙️ Configuration

Go to: 
Tools > Options > Branched Tabs

You’ll find options to:

- Enable/disable the extension
- Choose tab restore behavior:
  - Restore only
  - Replace and keep unsaved
  - Replace all and save unsaved


## 💡 How It Works

- On solution open or Git branch switch:
  - Previously open tabs for the current branch are restored
- On solution close or branch switch:
  - Current open files are saved to `.vs/BranchTabs.json`

Tabs are restored based on file paths. Unsaved files can optionally be saved automatically.


## 🧑‍💻 Contributing

Want to improve or extend the extension? Contributions are welcome!

1. Fork this repo
2. Clone and open the `.sln` file in Visual Studio
3. Build using the **Experimental Instance** of Visual Studio
4. Submit a PR!


## 📄 License

This project is licensed under the [MIT License](LICENSE).

---

## 📫 Contact

For issues or suggestions, feel free to open an [issue](https://github.com/MikeKatsoulakis/BranchedTabs/issues) or reach out via GitHub.
