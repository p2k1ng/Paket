﻿/// Contains NuGet support.
module Paket.NuGetCache

open System
open System.IO
open Newtonsoft.Json
open System.IO.Compression
open Paket.Logging
open System.Text

open Paket
open Paket.Domain
open Paket.Utils
open Paket.PackageSources
open Paket.Requirements
open FSharp.Polyfill
open System.Runtime.ExceptionServices

open System.Threading.Tasks

type NuGetResponseGetVersionsSuccess = string []
type NuGetResponseGetVersionsFailure =
    { Url : string; Error : ExceptionDispatchInfo }
    static member ofTuple (url,err) =
        { Url = url; Error = err }
type NuGetResponseGetVersions =
    | SuccessVersionResponse of NuGetResponseGetVersionsSuccess
    | ProtocolNotCached
    | FailedVersionRequest of NuGetResponseGetVersionsFailure
    member x.Versions =
        match x with
        | SuccessVersionResponse l -> l
        | ProtocolNotCached -> [||]
        | FailedVersionRequest _ -> [||]
    member x.IsSuccess =
        match x with
        | SuccessVersionResponse _ -> true
        | ProtocolNotCached -> false
        | FailedVersionRequest _ -> false
type NuGetResponseGetVersionsSimple = SafeWebResult<NuGetResponseGetVersionsSuccess>
type NuGetRequestGetVersions =
    { DoRequest : unit -> Async<NuGetResponseGetVersions>
      Url : string }
    static member ofFunc url f =
        { Url = url; DoRequest = f }
    static member ofSimpleFunc url (f: _ -> Async<NuGetResponseGetVersionsSimple>) =
        NuGetRequestGetVersions.ofFunc url (fun _ ->
            async {
                let! res = f()
                return
                    match res with
                    | SuccessResponse r -> SuccessVersionResponse r
                    | NotFound -> SuccessVersionResponse [||]
                    | UnknownError err -> FailedVersionRequest { Url = url; Error = err }
            })
    static member run (r:NuGetRequestGetVersions) : Async<NuGetResponseGetVersions> =
        async {
            try
                return! r.DoRequest()
            with e -> 
                return FailedVersionRequest { Url = r.Url; Error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture e }
        }
        

// An unparsed file in the nuget package -> still need to inspect the path for further information. After parsing an entry will be part of a "LibFolder" for example.
type UnparsedPackageFile =
    { FullPath : string
      PathWithinPackage : string }
    member x.BasePath =
        x.FullPath.Substring(0, x.FullPath.Length - (x.PathWithinPackage.Length + 1))

module NuGetConfig =
    open System.Text
    
    let writeNuGetConfig directory sources =
        let start = """<?xml version="1.0" encoding="utf-8"?>
<configuration>
    <packageSources>
    <!--To inherit the global NuGet package sources remove the <clear/> line below -->
    <clear />
"""
        let sb = StringBuilder start

        let i = ref 1
        for source in sources do
            sb.AppendLine(sprintf "    <add key=\"source%d\" value=\"%O\" />" !i source) |> ignore

        sb.Append("""
    </packageSources>
</configuration>""") |> ignore
        let text = sb.ToString()
        let fileName = Path.Combine(directory,Constants.NuGetConfigFile)
        if not <| File.Exists fileName then
            File.WriteAllText(fileName,text)
        else
            if File.ReadAllText(fileName) <> text then
                File.WriteAllText(fileName,text)
       
type FrameworkRestrictionsCache = string

type NuGetPackageCache =
    { SerializedDependencies : (PackageName * VersionRequirement * FrameworkRestrictionsCache) list
      PackageName : string
      SourceUrl: string
      Unlisted : bool
      DownloadUrl : string
      LicenseUrl : string
      Version: string
      CacheVersion: string }

    static member CurrentCacheVersion = "5.2"

// TODO: is there a better way? for now we use static member because that works with type abbreviations...
//module NuGetPackageCache =
    static member withDependencies (l:(PackageName * VersionRequirement * FrameworkRestrictions) list) d =
        { d with
            SerializedDependencies =
                l
                |> List.map (fun (n,v, restrictions) ->
                    let restrictionString = 
                        match restrictions with
                        | FrameworkRestrictions.AutoDetectFramework -> "AUTO"
                        | FrameworkRestrictions.ExplicitRestriction re ->
                            re.ToString()
                    n, v, restrictionString) }
    static member getDependencies (x:NuGetPackageCache) : (PackageName * VersionRequirement * FrameworkRestrictions) list  =
        x.SerializedDependencies
        |> List.map (fun (n,v,restrictionString) ->
            let restrictions =
                if restrictionString = "AUTO" then
                    FrameworkRestrictions.AutoDetectFramework
                else FrameworkRestrictions.ExplicitRestriction(Requirements.parseRestrictions restrictionString |> fst)
            n, v, restrictions)

let inline normalizeUrl(url:string) = url.Replace("https://","http://").Replace("www.","")

let getCacheFiles cacheVersion nugetURL (packageName:PackageName) (version:SemVerInfo) =
    let h = nugetURL |> normalizeUrl |> hash |> abs
    let prefix = 
        sprintf "%O.%s.s%d" packageName (version.Normalize()) h
    let packageUrl = 
        sprintf "%s_v%s.json" 
           prefix cacheVersion
    let newFile = Path.Combine(Constants.NuGetCacheFolder,packageUrl)
    let oldFiles =
        Directory.EnumerateFiles(Constants.NuGetCacheFolder, sprintf "%s*.json" prefix)
        |> Seq.filter (fun p -> Path.GetFileName p <> packageUrl)
        |> Seq.toList
    FileInfo(newFile), oldFiles

type ODataSearchResult =
    | EmptyResult
    | Match of NuGetPackageCache
module ODataSearchResult =
    let get x =
        match x with
        | EmptyResult -> failwithf "Cannot call get on 'EmptyResult'"
        | Match r -> r

let tryGetDetailsFromCache force nugetURL (packageName:PackageName) (version:SemVerInfo) : ODataSearchResult option =
    let cacheFile, oldFiles = getCacheFiles NuGetPackageCache.CurrentCacheVersion nugetURL packageName version
    oldFiles |> Seq.iter (fun f -> File.Delete f)
    if not force && cacheFile.Exists then
        let json = File.ReadAllText(cacheFile.FullName)
        let cacheResult =
            try
                let cachedObject = JsonConvert.DeserializeObject<NuGetPackageCache> json
                if (PackageName cachedObject.PackageName <> packageName) ||
                    (cachedObject.Version <> version.Normalize())
                then
                    traceVerbose (sprintf "Invalidating Cache '%s:%s' <> '%s:%s'" cachedObject.PackageName cachedObject.Version packageName.Name (version.Normalize()))
                    cacheFile.Delete()
                    None
                else
                    Some cachedObject
            with
            | exn ->
                cacheFile.Delete()
                if verbose then
                    traceWarnfn "Error while loading cache: %O" exn
                else
                    traceWarnfn "Error while loading cache: %s" exn.Message
                None
        match cacheResult with
        | Some res -> Some (ODataSearchResult.Match res)
        | None -> None
    else
        None

let getDetailsFromCacheOr force nugetURL (packageName:PackageName) (version:SemVerInfo) (get : unit -> ODataSearchResult Async) : ODataSearchResult Async =
    let cacheFile, oldFiles = getCacheFiles NuGetPackageCache.CurrentCacheVersion nugetURL packageName version
    oldFiles |> Seq.iter (fun f -> File.Delete f)
    let get() =
        async {
            let! result = get()
            match result with
            | ODataSearchResult.Match result ->
                File.WriteAllText(cacheFile.FullName,JsonConvert.SerializeObject(result))
            | _ ->
                // TODO: Should we cache 404? Probably not.
                ()
            return result
        }
    async {
        match tryGetDetailsFromCache force nugetURL packageName version with
        | None -> return! get()
        | Some res -> return res
    }


let fixDatesInArchive fileName =
    try
        use zipToOpen = new FileStream(fileName, FileMode.Open)
        use archive = new ZipArchive(zipToOpen, ZipArchiveMode.Update)
        let maxTime = DateTimeOffset.Now

        for e in archive.Entries do
            try
                let d = min maxTime e.LastWriteTime
                e.LastWriteTime <- d
            with
            | _ -> e.LastWriteTime <- maxTime
    with
    | exn -> traceWarnfn "Could not fix timestamps in %s. Error: %s" fileName exn.Message
    

let fixArchive fileName =
    if isMonoRuntime then
        fixDatesInArchive fileName

let GetLicenseFileName (packageName:PackageName) (version:SemVerInfo) = packageName.ToString() + "." + version.Normalize() + ".license.html"
let GetPackageFileName (packageName:PackageName) (version:SemVerInfo) = packageName.ToString() + "." + version.Normalize() + ".nupkg"

let inline isExtracted (directory:DirectoryInfo) (packageName:PackageName) (version:SemVerInfo) =
    let inDir f = Path.Combine(directory.FullName, f)
    let packFile = GetPackageFileName packageName version |> inDir
    let licenseFile = GetLicenseFileName packageName version |> inDir
    let fi = FileInfo(packFile)
    if not fi.Exists then false else
    if not directory.Exists then false else
    directory.EnumerateFileSystemInfos()
    |> Seq.exists (fun f -> f.FullName <> fi.FullName && f.FullName <> licenseFile)

let IsPackageVersionExtracted(config:ResolvedPackagesFolder, packageName:PackageName, version:SemVerInfo) =
    match config.Path with
    | Some target ->
        let targetFolder = DirectoryInfo(target)
        isExtracted targetFolder packageName version
    | None ->
        // Need to extract in .nuget dir?
        true

// cleanup folder structure
let rec private cleanup (dir : DirectoryInfo) =
    for sub in dir.GetDirectories() do
        let newName = Uri.UnescapeDataString(sub.FullName).Replace("%2B","+")
        let di = DirectoryInfo newName
        if sub.FullName <> newName && not di.Exists then
            if not di.Parent.Exists then
                di.Parent.Create()
            try
                Directory.Move(sub.FullName, newName)
            with
            | exn -> failwithf "Could not move %s to %s%sMessage: %s" sub.FullName newName Environment.NewLine exn.Message

            cleanup (DirectoryInfo newName)
        else
            cleanup sub

    for file in dir.GetFiles() do
        let newName = Uri.UnescapeDataString(file.Name).Replace("%2B","+")
        if newName.Contains "..\\" || newName.Contains "../" then
          failwithf "Relative paths are not supported. Please tell the package author to fix the package to not use relative paths. The invalid file was '%s'" file.FullName
        if newName.Contains "\\" || newName.Contains "/" then
          traceWarnfn "File '%s' contains back- or forward-slashes, probably because it wasn't properly packaged (for example with windows paths in nuspec on a unix like system). Please tell the package author to fix it." file.FullName
        let newFullName = Path.Combine(file.DirectoryName, newName)
        if file.Name <> newName && not (File.Exists newFullName) then
            let dir = Path.GetDirectoryName newFullName
            if not <| Directory.Exists dir then
                Directory.CreateDirectory dir |> ignore

            File.Move(file.FullName, newFullName)


let GetTargetUserFolder packageName (version:SemVerInfo) =
    DirectoryInfo(Path.Combine(Constants.UserNuGetPackagesFolder,packageName.ToString(),version.Normalize())).FullName
let GetTargetUserNupkg packageName (version:SemVerInfo) =
    let normalizedNupkgName = GetPackageFileName packageName version
    let path = GetTargetUserFolder packageName version
    Path.Combine(path, normalizedNupkgName)

let GetTargetUserToolsFolder packageName (version:SemVerInfo) =
    DirectoryInfo(Path.Combine(Constants.UserNuGetPackagesFolder,".tools",packageName.ToString(),version.Normalize())).FullName

/// Extracts the given package to the user folder
let rec ExtractPackageToUserFolder(fileName:string, packageName:PackageName, version:SemVerInfo, isCliTool, detailed) =
    async {
        let targetFolder =
            let dir =
                if isCliTool then
                    ExtractPackageToUserFolder(fileName, packageName, version, false, detailed) |> ignore
                    GetTargetUserToolsFolder packageName version
                else
                    GetTargetUserFolder packageName version
            DirectoryInfo(dir)

        use _ = Profile.startCategory Profile.Category.FileIO
        if isExtracted targetFolder packageName version |> not then
            Directory.CreateDirectory(targetFolder.FullName) |> ignore
            let fi = FileInfo fileName
            let targetPackageFileName = Path.Combine(targetFolder.FullName,fi.Name)
            if normalizePath fileName <> normalizePath targetPackageFileName then
                File.Copy(fileName,targetPackageFileName,true)

            ZipFile.ExtractToDirectory(fileName, targetFolder.FullName)

            let cachedHashFile = Path.Combine(Constants.NuGetCacheFolder,fi.Name + ".sha512")
            if not <| File.Exists cachedHashFile then
                let packageHash = getSha512File fileName
                File.WriteAllText(cachedHashFile,packageHash)

            File.Copy(cachedHashFile,targetPackageFileName + ".sha512")
            cleanup targetFolder
        return targetFolder.FullName
    }

/// Extracts the given package to the ./packages folder
let ExtractPackage(fileName:string, targetFolder, packageName:PackageName, version:SemVerInfo, detailed) =
    async {
        use _ = Profile.startCategory Profile.Category.FileIO
        let directory = DirectoryInfo(targetFolder)
        if isExtracted directory packageName version then
             if verbose then
                 verbosefn "%O %O already extracted" packageName version
        else
            Directory.CreateDirectory(targetFolder) |> ignore

            try
                fixArchive fileName
                ZipFile.ExtractToDirectory(fileName, targetFolder)
            with
            | exn ->
                let text = if detailed then sprintf "%s In rare cases a firewall might have blocked the download. Please look into the file and see if it contains text with further information." Environment.NewLine else ""
                let path = try Path.GetFullPath fileName with :? PathTooLongException -> sprintf "%s (!too long!)" fileName
                raise <| Exception(sprintf "Error during extraction of %s.%s%s" path Environment.NewLine text, exn)


            cleanup directory
            if verbose then
                verbosefn "%O %O unzipped to %s" packageName version targetFolder
        return targetFolder
    }

let CopyLicenseFromCache(config:ResolvedPackagesFolder, cacheFileName, packageName:PackageName, version:SemVerInfo, force) =
    async {
        try
            if String.IsNullOrWhiteSpace cacheFileName then return () else
            match config.Path with
            | Some packagePath ->
                let cacheFile = FileInfo cacheFileName
                if cacheFile.Exists then
                    let targetFile = FileInfo(Path.Combine(packagePath, "license.html"))
                    if not force && targetFile.Exists then
                        if verbose then
                           verbosefn "License %O %O already copied" packageName version
                    else
                        use _ = Profile.startCategory Profile.Category.FileIO
                        File.Copy(cacheFile.FullName, targetFile.FullName, true)
            | None -> ()
        with
        | exn -> traceWarnfn "Could not copy license for %O %O from %s.%s    %s" packageName version cacheFileName Environment.NewLine exn.Message
    }

/// Extracts the given package to the ./packages folder
let CopyFromCache(config:ResolvedPackagesFolder, cacheFileName, licenseCacheFile, packageName:PackageName, version:SemVerInfo, force, detailed) =
    async {
        match config.Path with
        | Some target ->
            let targetFolder = DirectoryInfo(target).FullName
            let fi = FileInfo(cacheFileName)
            let targetFile = FileInfo(Path.Combine(targetFolder, fi.Name))
            if not force && targetFile.Exists then
                if verbose then
                    verbosefn "%O %O already copied" packageName version
            else
                use _ = Profile.startCategory Profile.Category.FileIO
                CleanDir targetFolder
                File.Copy(cacheFileName, targetFile.FullName)
            try
                let! extracted = ExtractPackage(targetFile.FullName,targetFolder,packageName,version,detailed)
                do! CopyLicenseFromCache(config, licenseCacheFile, packageName, version, force)
                return Some extracted
            with
            | exn ->
                use _ = Profile.startCategory Profile.Category.FileIO
                File.Delete targetFile.FullName
                Directory.Delete(targetFolder,true)
                return! raise exn
        | None -> return None
    }

/// Puts the package into the cache
let CopyToCache(cache:Cache, fileName, force) =
    try
        use __ = Profile.startCategory Profile.Category.FileIO
        if Cache.isInaccessible cache then
            if verbose then
                verbosefn "Cache %s is inaccessible, skipping" cache.Location
        else
            let targetFolder = DirectoryInfo(cache.Location)
            if not targetFolder.Exists then
                targetFolder.Create()

            let fi = FileInfo(fileName)
            let targetFile = FileInfo(Path.Combine(targetFolder.FullName, fi.Name))

            if not force && targetFile.Exists then
                if verbose then
                    verbosefn "%s already in cache %s" fi.Name targetFolder.FullName
            else
                File.Copy(fileName, targetFile.FullName, force)
    with
    | _ ->
        Cache.setInaccessible cache
        reraise()

type SendDataModification =
    { LoweredPackageId : bool; NormalizedVersion : bool }

type GetVersionFilter =
    { ToLower : bool; NormalizedVersion : bool }

type UrlId =
    | GetVersion_ById of SendDataModification
    | GetVersion_Filter of SendDataModification * GetVersionFilter

type UrlToTry =
    { UrlId : UrlId; InstanceUrl : string }

    static member From id (p:Printf.StringFormat<_,_>) =
        Printf.ksprintf (fun s -> {UrlId = id; InstanceUrl = s}) p

type BlockedCacheEntry =
    { BlockedFormats : string list }

let private tryUrlOrBlacklistI =
    let tryUrlOrBlacklistInner (f : unit -> Async<obj>, isOk : obj -> bool) (cacheKey) =
        async {
            //try
            let! res = f ()
            return isOk res, res
        }
    let memoizedBlackList = memoizeAsyncEx tryUrlOrBlacklistInner
    fun f isOk cacheKey ->
            memoizedBlackList (f, isOk) (cacheKey)

let private tryUrlOrBlacklist (f: _ -> Async<'a>) (isOk : 'a -> bool) (source:NugetSource, id:UrlId) =
    let res =
        tryUrlOrBlacklistI
            (fun s -> async { let! r = f s in return box r })
            (fun s -> isOk (s :?> 'a))
            (source,id)
    match res with
    | SubsequentCall r -> SubsequentCall r
    | FirstCall t ->
        FirstCall (t |> Task.Map (fun (l, r) -> l, (r :?> 'a)))

let tryAndBlacklistUrl doWarn (source:NugetSource) (tryAgain : 'a -> bool) (f : string -> Async<'a>) (urls: UrlToTry list) : Async<'a>=
    async {
        let! tasks, resultIndex =
            urls
            |> Seq.map (fun url -> async {
                let cached = tryUrlOrBlacklist (fun () -> async { return! f url.InstanceUrl }) (tryAgain >> not) (source, url.UrlId)
                match cached with
                | SubsequentCall task ->
                    let! result = task |> Async.AwaitTask
                    if result then
                        let! result = f url.InstanceUrl
                        return Choice1Of3 result
                    else
                        return Choice3Of3 () // Url Blacklisted
                | FirstCall task ->
                    let! (isOk, res) = task |> Async.AwaitTask
                    if not isOk then
                        if doWarn then
                            eprintfn "Possible Performance degration, blacklist '%s'" url.InstanceUrl
                        return Choice2Of3 res
                    else
                        return Choice1Of3 res
                })
            |> Async.tryFindSequential (function | Choice1Of3 _ -> true | _ -> false)

        match resultIndex with
        | Some i ->
            return
                match tasks.[i].Result with
                | Choice1Of3 res -> res
                | _ -> failwithf "Unexpected value"
        | None ->
            let lastResult =
                tasks
                |> Seq.filter (fun t -> t.IsCompleted)
                |> Seq.map (fun t -> t.Result)
                |> Seq.choose (function
                    | Choice3Of3 _ -> None
                    | Choice2Of3 res -> Some res
                    | Choice1Of3 res -> Some res)
                |> Seq.tryLast

            return
                match lastResult with
                | Some res -> res
                | None ->
                    let urls = urls |> Seq.map (fun u -> u.InstanceUrl) |> fun s -> String.Join("\r\t - ", s)
                    failwithf "All possible sources are already blacklisted. \r\t - %s" urls
    }