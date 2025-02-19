using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace BadNews.Repositories.News
{
    public class NewsRepository : INewsRepository
    {
        private static readonly string recordSeparator = "<--e891b395-4498-4f93-84a5-19b867d826ae-->";
        private readonly string dataFileName;
        private string DataFilePath => $"./.db/{dataFileName}";

        private object dataFileLocker = new object();
        private Dictionary<string, long> indexIdToPosition;

        public NewsRepository(string dataFileName = "news.txt")
        {
            this.dataFileName = dataFileName;
            
            this.indexIdToPosition = new Dictionary<string, long>();

            ReadFromFile((meta, data, position) =>
            {
                var id = meta.ToString();
                indexIdToPosition[id] = position;
                return false;
            });

        }

        public void InitializeDataBase(IEnumerable<NewsArticle> articles)
        {
            var dataFileInfo = new FileInfo(DataFilePath);
            if (dataFileInfo.Exists)
                dataFileInfo.Delete();
            if (!dataFileInfo.Directory.Exists)
                dataFileInfo.Directory.Create();

            lock (dataFileLocker)
            {
                var file = dataFileInfo.Open(FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using (var fileWriter = new StreamWriter(file))
                {
                    foreach (var article in articles)
                    {
                        var storedArticle = new NewsArticle(article);
                        if (storedArticle.Id == Guid.Empty)
                            storedArticle.Id = Guid.NewGuid();
                        AppendArticle(fileWriter, storedArticle);
                    }
                }
            }
        }

        public NewsArticle GetArticleById(Guid id)
        {
            var idString = id.ToString();
            if (!indexIdToPosition.TryGetValue(idString, out var position))
                return null;
            
            var article = JsonConvert.DeserializeObject<NewsArticle>(ReadArticle(position).ToString());
            if (id != article.Id)
                throw new InvalidDataException();

            return article.IsDeleted ? null : article;
        }

        public IList<NewsArticle> GetArticles(Func<NewsArticle, bool> predicate = null)
        {
            var articles = new Dictionary<Guid, NewsArticle>();

            ReadFromFile((meta, data, position) =>
            {
                var id = Guid.Parse(meta.ToString());
                var obj = JsonConvert.DeserializeObject<NewsArticle>(data.ToString());
                if (id != obj.Id)
                    throw new InvalidDataException();
                if (obj.IsDeleted || predicate == null || predicate(obj))
                    articles[id] = obj;
                return false;
            });

            return articles
                .Where(it => !it.Value.IsDeleted)
                .Select(it => it.Value)
                .OrderByDescending(it => it.Date)
                .ToList();
        }

        public IList<int> GetYearsWithArticles()
        {
            var years = new Dictionary<Guid, DateTime>();

            ReadFromFile((meta, data, position) =>
            {
                var obj = JsonConvert.DeserializeObject<NewsArticle>(data.ToString());
                if (!obj.IsDeleted)
                    years[obj.Id] = obj.Date;
                else
                    years.Remove(obj.Id);
                return false;
            });

            return years.Select(it => it.Value.Year)
                .Distinct()
                .OrderByDescending(it => it)
                .ToArray();
        }

        public Guid CreateArticle(NewsArticle article)
        {
            if (article.Id != Guid.Empty)
                throw new InvalidOperationException("Creating article should not have id");

            lock (dataFileLocker)
            {
                var file = new FileStream(DataFilePath, FileMode.Append, FileAccess.Write, FileShare.None);
                using (var fileWriter = new StreamWriter(file))
                {
                    var storedArticle = new NewsArticle(article)
                    {
                        Id = Guid.NewGuid()
                    };
                    AppendArticle(fileWriter, storedArticle);

                    return storedArticle.Id;
                }
            }
        }

        public void DeleteArticleById(Guid id)
        {
            lock (dataFileLocker)
            {
                var file = new FileStream(DataFilePath, FileMode.Append, FileAccess.Write, FileShare.None);
                using (var fileWriter = new StreamWriter(file))
                {
                    var storedArticle = new NewsArticle()
                    {
                        Id = id,
                        IsDeleted = true
                    };

                    AppendArticle(fileWriter, storedArticle);
                }
            }
        }

        private void AppendArticle(StreamWriter file, NewsArticle article)
        {
            var meta = article.Id.ToString();
            var data = JsonConvert.SerializeObject(article, Formatting.Indented);
            file.WriteLine(recordSeparator);
            file.WriteLine(meta);
            file.WriteLine(data);
            indexIdToPosition[article.Id.ToString()] = file.BaseStream.Position;;
        }

        private StringBuilder ReadArticle(long startPosition)
        {
            if (!File.Exists(DataFilePath))
                return null;

            lock (dataFileLocker)
            {
                var file = new FileStream(DataFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var fileReader = new SeekableStreamTextReader(file, Encoding.UTF8);
                fileReader.Seek(startPosition, SeekOrigin.Begin);

                var objectLine = 0;
                var data = new StringBuilder();

                var line = fileReader.ReadLine();
                while (line != null)
                {
                    if (line != recordSeparator)
                    {
                        if (objectLine++ > 0)
                            data.Append(line);
                    }
                    else
                    {
                        return data;
                    }

                    line = fileReader.ReadLine();
                }

                return data;
            }
        }

        private void ReadFromFile(
            Func<StringBuilder, StringBuilder, long, bool> onObjectRead)
        {
            if (!File.Exists(DataFilePath))
                return;

            lock (dataFileLocker)
            {
                var file = new FileStream(DataFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var fileReader = new SeekableStreamTextReader(file, Encoding.UTF8);
                var objectLine = 0;
                var metaBuilder = new StringBuilder();
                var dataBuilder = new StringBuilder();
                var lineStartPos = fileReader.UsedBytes;
                var objectStartPos = (long) -1;

                var line = fileReader.ReadLine();
                while (line != null)
                {
                    if (line != recordSeparator)
                    {
                        if (objectLine++ > 0)
                        {
                            dataBuilder.Append(line);
                        }
                        else
                        {
                            metaBuilder.Append(line);
                            objectStartPos = lineStartPos;
                        }
                    }
                    else
                    {
                        if (metaBuilder.Length > 0 || dataBuilder.Length > 0)
                        {
                            if (onObjectRead(metaBuilder, dataBuilder, objectStartPos))
                                return;
                        }

                        objectLine = 0;
                        metaBuilder = new StringBuilder();
                        dataBuilder = new StringBuilder();
                    }
                        
                    lineStartPos = fileReader.UsedBytes;
                    line = fileReader.ReadLine();
                }

                if (dataBuilder.Length > 0)
                {
                    if (onObjectRead(metaBuilder, dataBuilder, objectStartPos))
                        return;
                }
            }
        }
    }
}
