# Plant git repo information in .NET assemblies #

gitinfoplanter.exe automates the process of reading your git repo status and embedding it as an easy to use string in an assembly level attribute of your .NET executable.

## Git Repo String ##

So how does a version string look?  
The best way to answer this is to simply run the info planter with --version to see a sample:

> $ ./daemaged.gitinfoplanter.exe --version  
> daemaged.gitinfoplanter version "1.1.0.0.4658/1.1/20/1e5356fd2bdbde4b5c82552844e6eb5ed6d5a9f2"

The version string is the part that says: *1.1.0.0.4658/1.1/20/1e5356fd2bdbde4b5c82552844e6eb5ed6d5a9f2*  
but what does it really mean?
<img src="https://raw.github.com/damageboy/daemaged.gitinfoplanter/master/version-info-explanation.svg" height="258" width="750" />

- The first part, 1.1.0.0.4658 in this case, is really composed of two parts
 - The Assembly Version attribute value (This is the normal value that get included in every .NET AssemblyInfo.cs file, 1.0.0.0 in this case
 - The build-day, expressed as number of days since a specified "epoch-day", 1/1/2000 by default, 4658 days in this case, any base date is really good here, as long as it's constant..., good options are your anniversary, birthday, child's birth-day, historical event or whatever :)
- The second part is best described as the "what-to-checkout or clone" field... Normally it will be the branch name of the build, **but it doesn't have to be**: if the build was made of a *valid* tag release, it will contain the name of the tag... What makes a valid tag release?
  - The commit has to have a tag (duh)
  - The tag has to be pushed
  - There are no local modifications
  - The same commit is also pushed to the origin
- Next we have the revision # which can be thought of as some incremental number that provides an SVN-esque revision #. In reality it's just a count of the commits in the current branch...  
However, this revision # may have appendages:
  - +N in case the local commit is ahead of the origin, and N denoted by how many local commits
  - The string "M" in case the build was done off of a repo with local modifications
 - Last but not least, the full commit hash is embedded as a string, for safe keeping

## Where/How does this get embedded? ##
The git info planter uses cecil to re-write the assembly and embed two assembly level attributes:

- `[AssemblyFileVersionAttribute]` will be embedded with the first part of the full string as it's value (1.1.0.0.4658 in the previous example)
- `[AssemblyInformationalVersionAttribute]` will be embedded with the full string as it's value

So, you can think of the resulting assemblly *as-if* someone went back to your code, and edited you AssemblyInfo.cs file from this:

```c#
using System.Reflection;

[assembly: AssemblyTitle("Daemaged.GitInfoPlanter")]
[assembly: AssemblyDescription("Plant git information into .NET assemblies")]
[assembly: AssemblyCompany("Damage INC.")]
[assembly: AssemblyProduct("Daemaged.GitInfoPlanter")]
[assembly: AssemblyCopyright("Copyright (C) Dan Shechter, Inc. 2010")]
[assembly: AssemblyVersion("1.1.0.0")]
```

To this:
```c#
using System.Reflection;

[assembly: AssemblyTitle("Daemaged.GitInfoPlanter")]
[assembly: AssemblyDescription("Plant git information into .NET assemblies")]
[assembly: AssemblyCompany("Damage INC.")]
[assembly: AssemblyProduct("Daemaged.GitInfoPlanter")]
[assembly: AssemblyCopyright("Copyright (C) Dan Shechter, Inc. 2010")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersionAttribute("1.1.0.0.4658")]
[assembly: AssemblyInformationalVersionAttribute("1.1.0.0.4658/1.1/20/1e5356fd2bdbde4b5c82552844e6eb5ed6d5a9f2")]
```

Of course, no real changes to the code were actually made, however the resulting binary will contain the additional attributes

## How do I retrieve these attributes at runtime? ##

Well, it's obviously a very idea to be able to actually read these things when displaying an about dialog, or when a `--version` option is perhaps specified of command-line...  
It's a very simple chore to accomplish, in c# for example, all that one needs to do is:
```c#
var versionInfo = 
  asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
     .Cast<AssemblyInformationalVersionAttribute>().FirstOrDefault();
var versionString = 
  versionInfo == null ? 
   "No specific version info embedded" : 
   versionInfo.InformationalVersion;
Console.WriteLine("MyProduct - {0}", versionString);
```

## OK, I'm sold, how do I put this in my build process? ##
OK, so that is perhaps a slightly weak side of this project... it's integration level with IDE etc.
The best way would be to simply add it as an after build step in Visual Studio / MonoDevelop / MSBuild.

The way I personally do it is by manually editing the .csproj and adding an `<AfterBuild>` target