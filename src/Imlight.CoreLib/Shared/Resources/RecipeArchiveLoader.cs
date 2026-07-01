/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Imcodec.Wad;
using Imlight.Common;

namespace Imlight.CoreLib.Shared.Resources;

/// <summary>
/// Loads the Recipes-WorldData.wad archive into memory. Recipe templates physically
/// live in this separate WAD (they are not referenced by Root.wad's TemplateManifest),
/// so this loader runs in parallel to <see cref="RootArchiveLoader"/>.
/// </summary>
internal static class RecipeArchiveLoader {

    internal const string WAD_NAME = "Recipes-WorldData.wad";

    internal static bool IsLoaded { get; private set; }
    internal static Archive GetRecipeWad() => s_recipeWad;
    private static readonly Lock s_lock = new();
    private static Archive s_recipeWad;

    /// <summary>
    /// Reloads the Recipes-WorldData.wad file into memory.
    /// </summary>
    public static void ReloadRecipeWad() {
        Logger.Information("Loading {0} into memory..", Logger.Args(WAD_NAME));

        s_recipeWad = ResourceWad();
        if (s_recipeWad is not null) {
            IsLoaded = true;
            Logger.Information("{0} successfully loaded into memory.", Logger.Args(WAD_NAME));
        }
    }

    /// <summary>
    /// Retrieves a dictionary of file records and memory streams for files within a specified directory.
    /// </summary>
    /// <param name="directoryName">The name of the directory.</param>
    /// <returns>A dictionary containing file records as keys and memory streams as values.</returns>
    internal static Dictionary<FileEntry, Memory<byte>?> GetDirectoryStream(string directoryName) {
        lock (s_lock) {
            if (s_recipeWad is null) {
                ReloadRecipeWad();
            }

            var files = new Dictionary<FileEntry, Memory<byte>?>();
            if (s_recipeWad is null) {
                return files;
            }

            foreach (var file in s_recipeWad.Files
                .Where(x => x.Key.StartsWith(directoryName) && x.Key != directoryName)) {
                var fileName = file.Key;
                var fileRecord = file.Value;

                var stream = s_recipeWad.OpenFile(fileName);
                files.Add(fileRecord.Value, stream);
            }

            return files;
        }
    }

    private static Archive ResourceWad() {
        // Check if the file is already cached. If it is, just return that.
        var cachedWad = LocalWadCache.GetCachedWad(WAD_NAME);
        if (cachedWad is not null) {
            return cachedWad;
        }

        // Otherwise, download it from the patch server.
        // If Imlight is running without the patch server, we'll just return null.
        if (!PatchServerFascade.EndpointReached) {
            Logger.Error("Patch server is not reachable. Cannot load {0}.", Logger.Args(WAD_NAME));

            return null;
        }

        if (!PatchServerFascade.DownloadWadFromPatchServer(WAD_NAME, out var stream)) {
            Logger.Error("Failed to download wad {WadName} from patch server", Logger.Args(WAD_NAME));

            return null;
        }

        // If we successfully downloaded it, we'll also cache it so we don't have to do that again.
        stream.Seek(0, SeekOrigin.Begin);
        var wad = ArchiveParser.Parse(stream);
        LocalWadCache.CacheWad(WAD_NAME, wad);

        return wad;
    }

}
