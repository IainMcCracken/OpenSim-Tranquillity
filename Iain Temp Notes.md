CoreJ2K.Skia decodes JPEG2000 files into SKColorType.Rgba8888 and SKAlphaType.Unpremul

Can output to SKBitmap or SKImage with the image. The main code outputs a SKBitmap, but a SKImage will work in the InterleavedImage

Warp3DImageModule -- does fake J2000 stuff -- should be fixed.

SkiaSharp can save:
JPEG -- any quality level
PNG
WEBP -- only 100% quality level (not particularly useful) at other quality levels, Microsoft's image handling code barfs in Paint.NET trying to load the file.


OpenSim Startup:


            configSource.AddSwitch("Startup", "gui");
            configSource.AddSwitch("Startup", "console");

backup: bool -- are we running in the background (default false)
save_crashes: bool -- are we saving crashes (default false)
crash_dir: string -- crash dump dir (default "crashes")
inimaster: string -- pathname/URI of defaults INI (default "OpenSimDefaults.ini") -- pathname can be relative from CWD or absolute
inifile: string -- pathname/URI of main INI (default "OpenSim.ini") -- pathname can be relative from CWD or absolute
inidirectory: string -- pathname to where other .ini files are stored (default "config") -- will load anything with .ini extension


File loading:
* Load defaults first:
  * defaults first -- default ./OpenSimDefaults.ini
* Merge in the main config:
  * main ini -- default ./OpenSim.ini
  * merge in all includes from these.
* Merge in "overrides" from the inidirectory -- default ./config/
  * Read any file with .ini extension
  * merge in all includes.



Robust params:

inifile
logfile

# WHERE

TerrainSplat and friends.

