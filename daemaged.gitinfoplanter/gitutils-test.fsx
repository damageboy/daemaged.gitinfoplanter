#I "..\deplibs"

#r "NGit.dll"
#r "Sharpen.dll"
#r "Mono.Cecil.dll"
#r "Mono.Cecil.Pdb.dll"

open System
open Sharpen
open Mono.Cecil
open Mono.Cecil.Pdb
open System
open System.IO
open System.Collections.Generic
open System.Text.RegularExpressions
open System.Reflection;
open Microsoft.FSharp.Text
open System.Reflection

open NGit
open NGit.Api
open NGit.Storage.File
open NGit.Revwalk
open NGit.Treewalk
open NGit.Treewalk.Filter

#load "gitutils.fs"

let repoPath = @"c:\\projects\\private\\sharptrader.git"

let notrepoPath = @"c:\\projects\\private\\sharptrader.git\src"

open gitutils

let r = repoPath |> buildRepo

let nr = notrepoPath |> buildRepo

r |> gitutils.getRepoStatus |> gitutils.modifiedPaths |> gitutils.filterSubmodules r
