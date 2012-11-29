namespace Daemaged.GitInfoPlanter
open System
module cmdlinefixer =
  let trimMatchingQuotes (c : char) (s : string)  =
    if (s.Length >= 2) && (s.[0] = c) && (s.[s.Length - 1] = c) then
      s.Substring(1, s.Length - 2)
    else
      s
  
  let trimMatchingDblQuotes = trimMatchingQuotes '\"'
  
  let split (s : string) controller =  
    let splitIdx = s |> Seq.mapi (fun i x -> (i, x)) |> Seq.filter (fun x -> controller (snd x)) |> Seq.map fst
    let x = Seq.append (Seq.append [0] splitIdx) [s.Length]
    x |> Seq.pairwise |> Seq.map (fun (x, y) -> s.Substring(x, y-x))
  
  let trim (s : string) = s.Trim()
  
  let splitCmdLine s =
    let inQuotes = ref false
    split s (fun c -> 
      if c = '\"' then 
        inQuotes := not !inQuotes
      (not !inQuotes) && (c = ' ')
    ) |> Seq.map trim |> Seq.map trimMatchingDblQuotes

  //let x = "--parallel --search-path \\\"C:\\Program Files (x86)\\Reference Assemblies\\Microsoft\\Framework\\.NETFramework\\v4.5\\\" --skip-missing --basedate 2000-01-01 --repo C:\\projects\\sharptrader\\src\\SharpTrader.Developer.Dist\\/../../ C:\\projects\\sharptrader\\src\\SharpTrader.Developer.Dist\\/../../dist/win-debug-x64/SharpTrader.Developer.Dist/bin/SharpTrader.Configurator.dll C:\\projects\\sharptrader\\src\\SharpTrader.Developer.Dist\\/../../dist/win-debug-x64/SharpTrader.Developer.Dist/bin"
  //splitCmdLine x