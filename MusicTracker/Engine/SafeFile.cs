using System.IO;
using System.Text;

namespace MusicTracker.Engine
{
    /// <summary>
    /// Écriture de fichier ATOMIQUE, pour tout ce dont la perte serait irrécupérable — un morceau, les réglages,
    /// la liste des récents.
    ///
    /// <see cref="File.WriteAllText(string,string)"/> TRONQUE le fichier existant avant d'écrire : une coupure de
    /// courant, un disque plein ou une clé USB retirée au mauvais moment détruit l'ancienne version sans avoir
    /// écrit la nouvelle. Le fichier — souvent la seule copie — est alors perdu.
    ///
    /// On écrit donc d'abord un fichier temporaire à côté, on le vide sur le disque, puis on le substitue en une
    /// seule opération du système de fichiers. À tout instant, il existe une version complète : l'ancienne ou la
    /// nouvelle, jamais un fichier à moitié écrit.
    /// </summary>
    public static class SafeFile
    {
        /// <summary>Écrit le texte à <paramref name="path"/> sans jamais laisser le fichier dans un état partiel.
        /// Conserve l'ancienne version tant que la nouvelle n'est pas intégralement sur le disque.</summary>
        public static void WriteAllText(string path, string text, Encoding encoding = null)
        {
            encoding = encoding ?? new UTF8Encoding(false);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Le temporaire vit dans le MÊME dossier : la substitution doit rester sur le même volume, sinon elle
            // dégénère en copie et perd son atomicité.
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, encoding))
            {
                w.Write(text);
                w.Flush();
                fs.Flush(true);          // force l'écriture jusqu'au disque, pas seulement au cache de l'OS
            }

            if (File.Exists(path))
            {
                // File.Replace est la substitution atomique de Windows. Elle échoue sur certains systèmes de
                // fichiers (réseau, FAT) : on retombe alors sur delete+move, moins sûr mais toujours meilleur
                // qu'une troncature — le temporaire est complet avant qu'on touche à l'original.
                try { File.Replace(tmp, path, null); return; }
                catch (IOException) { }
                catch (System.PlatformNotSupportedException) { }
                catch (System.UnauthorizedAccessException) { }
                File.Delete(path);
            }
            File.Move(tmp, path);
        }
    }
}
