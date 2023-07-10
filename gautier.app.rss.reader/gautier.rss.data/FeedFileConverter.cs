﻿using System.Data.SQLite;
using System.ServiceModel.Syndication;
using System.Text;

using gautier.rss.data.RSSDb;

namespace gautier.rss.data
{
    public static class FeedFileConverter
    {
        private const char _Tab = '\t';

        /// <summary>
        /// Designed to generate static local files even if they are later accidentally deleted.
        /// </summary>
        public static void CreateStaticFeedFiles(string feedSaveDirectoryPath, string feedDbFilePath, Feed[] feedInfos)
        {
            if (Directory.Exists(feedSaveDirectoryPath) == false)
            {
                Directory.CreateDirectory(feedSaveDirectoryPath);
            }

            string ConnectionString = SQLUtil.GetSQLiteConnectionString(feedDbFilePath, 3);

            using SQLiteConnection SQLConn = SQLUtil.OpenSQLiteConnection(ConnectionString);

            /*
             * At this point, in this version, the feed rows are statically fed through the software application.
             * NOTE TO SELF ... change this to drive exclusively off "both" a simple configuration file and existing database.
             *      In the future the way it will work is the program will look for the existence of an RSS database.
             *          If one exists, then it will load the feed entries into memory and append any entries from a file
             *            that does not already exist in the in-memory representation.
             *          If an rss db in SQLite format does not exist, then it will be created. The file entries will 
             *            populate the database (as it currently does in the late June/early July 2023 design).
             *            
             *          The primary focus of these design points is to assist future development and troubleshooting.
             *          
             *          The UI version of this program will not have these requirements since RSS feed configuration will be
             *             driven by a UI screen and not subject to self-sustained start-up automation as is the case here.
             */
            foreach (var FeedInfo in feedInfos)
            {
                string FeedFilePath = GetFeedFilePath(feedSaveDirectoryPath, FeedInfo);

                /*
                 * VERY IMPORTANT
                 *      This code path is the central nexus that determines if the entire program
                 *         is a naive implementation versus an actual RSS implementation.
                 *         Feeds should update at regular intervals without causing issues
                 *         related to accessing the source websites too often.
                 */
                bool Exists = FeedReader.Exists(SQLConn, FeedInfo.FeedName);

                /*
                 * Default assumption is the file should be created when there is no file.
                 * This should be overriden however when the logic shows a feed was already
                 *   accessed over the network within the upper time limit of 60 minutes.
                 */
                bool ShouldCacheFileBeCreated = (File.Exists(FeedFilePath) == false);

                /*
                 * An RSS feed database table entry exists and should be used to determine
                 *    if the feed should be retrieved versus reusing a local cache file.
                 */
                if (Exists)
                /*
                 * Go into a complex logic cycle.
                 *      The main goal is to access the feed based on the columns:
                            "last_retrieved",
                            "retrieve_limit_hrs",
                            "retention_days"

                    Remember to set the ShouldCacheFileBeCreated = false when
                        there is an indication a feed website was recently accessed.
                    Even when there is no local file but the feed was accessed over the network,
                    that situation must still be respected to avoid future access issues.
                 */
                {
                    if (File.Exists(FeedFilePath) == false)
                    {
                        ShouldCacheFileBeCreated = true;
                    }
                }

                /*
                 * A cached file may not exist. 
                 * Indicates a situation where a feed table entry does not exist and no file exists.
                 *      That can happen during testing, development and modification of the system.
                 *      In all cases, re-do the local cache files. This will also trigger regeneration
                 *      of the feed table entry after the transformation/translation stage.
                 */
                if (ShouldCacheFileBeCreated)
                {
                    RSSNetClient.CreateRSSFeedFile(FeedInfo.FeedUrl, FeedFilePath);
                }
            }

            return;
        }

        public static void TransformStaticFeedFiles(string feedSaveDirectoryPath, Feed[] feedInfos)
        {
            SortedList<string, List<FeedArticle>> Feeds = TransformMSSyndicationToFeedArticles(feedSaveDirectoryPath, feedInfos);

            foreach (var FeedInfo in feedInfos)
            {
                if (Feeds.ContainsKey(FeedInfo.FeedName) == false)
                {
                    continue;
                }

                WriteRSSArticlesToFile(feedSaveDirectoryPath, FeedInfo, Feeds[FeedInfo.FeedName]);
            }

            return;
        }

        private static void WriteRSSArticlesToFile(string feedSaveDirectoryPath, Feed feedInfo, List<FeedArticle> feedArticles)
        {
            StringBuilder RSSFeedFileOutput = new();
            string NormalizedFeedFilePath = GetNormalizedFeedFilePath(feedSaveDirectoryPath, feedInfo);

            using (StreamWriter RSSFeedFile = new(NormalizedFeedFilePath, false))
            {
                foreach (var Article in feedArticles)
                {
                    string HeadlineText = Article.HeadlineText;
                    string ArticleSummary = Article.ArticleSummary;
                    string ArticleText = Article.ArticleText;
                    string ArticleDate = Article.ArticleDate;
                    string ArticleUrl = Article.ArticleUrl;

                    RSSFeedFileOutput.AppendLine($"URL{_Tab}{ArticleUrl}");
                    RSSFeedFileOutput.AppendLine($"DATE{_Tab}{ArticleDate}");
                    RSSFeedFileOutput.AppendLine($"HEAD{_Tab}{HeadlineText}");
                    RSSFeedFileOutput.AppendLine($"TEXT{_Tab}{ArticleText}");
                    RSSFeedFileOutput.AppendLine($"SUM{_Tab}{ArticleSummary}");
                }

                RSSFeedFile.Write(RSSFeedFileOutput.ToString());

                RSSFeedFile.Flush();
                RSSFeedFile.Close();
            }

            RSSFeedFileOutput.Clear();

            return;
        }

        private static SortedList<string, List<FeedArticle>> TransformMSSyndicationToFeedArticles(string feedSaveDirectoryPath, Feed[] feedInfos)
        {
            SortedList<string, List<FeedArticle>> FeedArticles = new();

            foreach (var FeedInfo in feedInfos)
            {
                string RSSFeedFilePath = GetFeedFilePath(feedSaveDirectoryPath, FeedInfo);

                SyndicationFeed RSSFeed = RSSNetClient.CreateRSSSyndicationFeed(RSSFeedFilePath);

                if (RSSFeed.Items.Any())
                {
                    if (FeedArticles.ContainsKey(FeedInfo.FeedName) == false)
                    {
                        FeedArticles[FeedInfo.FeedName] = new();
                    }

                    List<FeedArticle> Articles = FeedArticles[FeedInfo.FeedName];

                    foreach (var RSSItem in RSSFeed.Items)
                    {
                        FeedArticle FeedItem = CreateRSSFeedArticle(FeedInfo, RSSItem);

                        Articles.Add(FeedItem);
                    }
                }
            }

            return FeedArticles;
        }

        private static FeedArticle CreateRSSFeedArticle(Feed feed, SyndicationItem syndicate)
        {
            string ArticleText = string.Empty;
            string ContentType = $"{syndicate.Content?.Type}";

            switch (ContentType)
            {
                case "TextSyndicationContent":
                    {
                        TextSyndicationContent? Content = syndicate.Content as TextSyndicationContent;

                        ArticleText = Content?.Text ?? string.Empty;
                    }
                    break;
                case "UrlSyndicationContent":
                    {
                        UrlSyndicationContent? Content = syndicate.Content as UrlSyndicationContent;

                        ArticleText = Content?.Url.AbsoluteUri ?? string.Empty;
                    }
                    break;
                case "XmlSyndicationContent":
                    {
                        XmlSyndicationContent? Content = syndicate.Content as XmlSyndicationContent;

                        ArticleText = Content?.ToString() ?? string.Empty;
                    }
                    break;
            }

            string ArticleUrl = string.Empty;

            foreach (var Url in syndicate.Links)
            {
                ArticleUrl = $"{Url.GetAbsoluteUri()}";

                break;
            }

            DateTime ArticleDate = syndicate.PublishDate.LocalDateTime;

            if (ArticleDate.Year < 0002)
            {
                ArticleDate = syndicate.LastUpdatedTime.LocalDateTime;
            }

            string ArticleDateText = ArticleDate.ToString("MM/dd/yyyy hh:mm tt");

            return new FeedArticle
            {
                FeedName = feed.FeedName,
                HeadlineText = syndicate.Title.Text,
                ArticleSummary = syndicate.Summary.Text,
                ArticleText = ArticleText,
                ArticleDate = ArticleDateText,
                ArticleUrl = ArticleUrl,
                RowInsertDateTime = DateTime.Now.ToString()
            };
        }

        private static string GetFeedFilePath(string feedSaveDirectoryPath, Feed feedInfo)
        {
            return Path.Combine(feedSaveDirectoryPath, $"{feedInfo.FeedName}.xml");
        }

        private static string GetNormalizedFeedFilePath(string feedSaveDirectoryPath, Feed feedInfo)
        {
            return Path.Combine(feedSaveDirectoryPath, $"{feedInfo.FeedName}.txt");
        }

        public static Feed[] GetStaticFeedInfos(string feedsFilePath)
        {
            List<Feed> Feeds = new();

            SortedList<string, string> FeedByName = new();

            using (StreamReader FeedsReader = new(feedsFilePath))
            {
                while (FeedsReader.EndOfStream == false)
                {
                    string FeedLine = $"{FeedsReader.ReadLine()}";

                    if (string.IsNullOrWhiteSpace(FeedLine))
                    {
                        continue;
                    }

                    string[] Columns = FeedLine.Split(_Tab);

                    if (Columns.Length < 1)
                    {
                        continue;
                    }

                    string FeedName = Columns[0];
                    string FeedUrl = Columns[1];

                    /*
                     * If there are duplicate feed entries, the structure of the collection
                     *   is defined such that the most recent entry prevails.
                     */
                    FeedByName[FeedName] = FeedUrl;
                }
            }

            foreach (string FeedName in FeedByName.Keys)
            {
                Feed FeedEntry = new()
                {
                    FeedName = FeedName,
                    FeedUrl = FeedByName[FeedName]
                };

                Feeds.Add(FeedEntry);
            }

            return Feeds.ToArray();
        }
    }
}
