﻿using Emby.Plugins.JavScraper.Scrapers;
using Emby.Plugins.JavScraper.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

#if __JELLYFIN__

using Microsoft.Extensions.Logging;

#else

using MediaBrowser.Model.Logging;

#endif

using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Plugins.JavScraper
{
    public class JavMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
    {
        private readonly IHttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly IProviderManager providerManager;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IApplicationPaths _appPaths;

        private List<AbstractScraper> scrapers;
        public ImageProxyService ImageProxyService { get; }

        public JavMovieProvider(ILogger logger, IProviderManager providerManager, IHttpClient httpClient, IJsonSerializer jsonSerializer, IFileSystem fileSystem, IApplicationPaths appPaths)
        {
            _logger = logger;
            this.providerManager = providerManager;
            _httpClient = httpClient;
            _jsonSerializer = jsonSerializer;
            _appPaths = appPaths;
            scrapers = GetScrapers(null, logger);
            ImageProxyService = new ImageProxyService(jsonSerializer, logger, fileSystem, appPaths);
        }

        public static List<AbstractScraper> GetScrapers(HttpClientHandler handler = null, ILogger log = null)
        {
            var ls = new List<AbstractScraper>();
            var base_type = typeof(AbstractScraper);
            var types = System.Reflection.Assembly.GetExecutingAssembly().GetTypes()
                 .Where(o => base_type != o && base_type.IsAssignableFrom(o))
                 .ToList();
            var p1 = typeof(HttpClientHandler);
            var p2 = typeof(ILogger);
            foreach (var type in types)
            {
                foreach (var c in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).OrderByDescending(o => o.GetParameters()?.Count() ?? 0))
                {
                    var ps = c.GetParameters();
                    var param = new List<object>();
                    var notfound = false;
                    foreach (var p in ps)
                    {
                        if (p.ParameterType == p1 || p1.IsAssignableFrom(p.ParameterType))
                            param.Add(handler);
                        else if (p.ParameterType == p2)
                            param.Add(log);
                        else
                        {
                            notfound = true;
                            break;
                        }
                    }
                    if (notfound)
                        continue;
                    try
                    {
                        var cc = Activator.CreateInstance(type, param.ToArray()) as AbstractScraper;
                        ls.Add(cc);
                    }
                    catch { }
                }
            }
            return ls;
        }

        public int Order => 4;

        public string Name => Plugin.NAME;

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            _logger?.Info($"{Name}-{nameof(JavMovieProvider)}-{nameof(GetImageResponse)} {url}");
            return ImageProxyService.GetImageResponse(url, cancellationToken);
        }

        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var metadataResult = new MetadataResult<Movie>();
            JavVideoIndex index = null;

            _logger?.Info($"{Name}-{nameof(GetMetadata)} info:{_jsonSerializer.SerializeToString(info)}");

            if ((index = info.GetJavVideoIndex(_jsonSerializer)) == null)
            {
                var res = await GetSearchResults(info, cancellationToken).ConfigureAwait(false);
                if (res.Count() == 0 || (index = res.FirstOrDefault().GetJavVideoIndex(_jsonSerializer)) == null)
                {
                    _logger?.Info($"{Name}-{nameof(GetMetadata)} name:{info.Name} not found 0.");
                    return metadataResult;
                }
            }

            if (index == null)
            {
                _logger?.Info($"{Name}-{nameof(GetMetadata)} name:{info.Name} not found 1.");
                return metadataResult;
            }

            var sc = scrapers.FirstOrDefault(o => o.Name == index.Provider);
            if (sc == null)
                return metadataResult;

            var m = await sc.Get(index);

            if (m == null)
            {
                _logger?.Info($"{Name}-{nameof(GetMetadata)} name:{info.Name} not found 2.");
                return metadataResult;
            }

            _logger?.Info($"{Name}-{nameof(GetMetadata)} name:{info.Name} {_jsonSerializer.SerializeToString(m)}");

            metadataResult.HasMetadata = true;
            metadataResult.QueriedById = true;

            //忽略部分类别
            if (m.Genres?.Any() == true)
            {
                m.Genres.RemoveAll(o => Plugin.Instance?.Configuration?.IsIgnoreGenre(o) == true);
                if (Plugin.Instance?.Configuration?.GenreIgnoreActor == true && m.Actors?.Any() == true)
                    m.Genres.RemoveAll(o => m.Actors.Contains(o));
            }

            //从标题结尾处移除女优的名字
            if (Plugin.Instance?.Configuration?.GenreIgnoreActor == true && m.Actors?.Any() == true && string.IsNullOrWhiteSpace(m.Title) == false)
            {
                var title = m.Title?.Trim();
                bool found = false;
                do
                {
                    found = false;
                    foreach (var actor in m.Actors)
                    {
                        if (title.EndsWith(actor))
                        {
                            title = title.Substring(0, title.Length - actor.Length).TrimEnd().TrimEnd(",， ".ToArray()).TrimEnd();
                            found = true;
                        }
                    }
                } while (found);
                m.Title = title;
            }

            if (Plugin.Instance?.Configuration?.AddChineseSubtitleGenre == true &&
                (info.Name.EndsWith("-C", StringComparison.OrdinalIgnoreCase) || info.Name.EndsWith("-C2", StringComparison.OrdinalIgnoreCase)))
            {
                const string CHINESE_SUBTITLE_GENRE = "中文字幕";
                if (m.Genres == null)
                    m.Genres = new List<string>() { CHINESE_SUBTITLE_GENRE };
                else if (m.Genres.Contains(CHINESE_SUBTITLE_GENRE) == false)
                    m.Genres.Add("中文字幕");
            }

            //格式化标题
            string name = $"{m.Num} {m.Title}";
            if (string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration?.TitleFormat) == false)
            {
                var empty = Plugin.Instance?.Configuration?.TitleFormatEmptyValue ?? string.Empty;
                name = Plugin.Instance.Configuration.TitleFormat;

                void Replace(string key, string value)
                {
                    var _index = name.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                    if (_index < 0)
                        return;

                    if (string.IsNullOrEmpty(value))
                        value = empty;

                    do
                    {
                        name = name.Remove(_index, key.Length);
                        name = name.Insert(_index, value);
                        _index = name.IndexOf(key, _index + value.Length, StringComparison.OrdinalIgnoreCase);
                    } while (_index >= 0);
                }

                Replace("%num%", m.Num);
                Replace("%title%", m.Title);
                Replace("%actor%", m.Actors?.Any() == true ? string.Join(", ", m.Actors) : null);
                Replace("%actor_first%", m.Actors?.FirstOrDefault());
                Replace("%set%", m.Set);
                Replace("%director%", m.Director);
                Replace("%date%", m.Date);
                Replace("%year%", m.GetYear()?.ToString());
                Replace("%month%", m.GetMonth()?.ToString("00"));
                Replace("%studio%", m.Studio);
                Replace("%maker%", m.Maker);
            }

            metadataResult.Item = new Movie
            {
                Name = name,
                Overview = m.Plot,
                ProductionYear = m.GetYear(),
                OriginalTitle = m.Title,
                Genres = m.Genres?.ToArray() ?? new string[] { },
                CollectionName = m.Set,
                SortName = m.Num,
                ForcedSortName = m.Num,
                ExternalId = m.Num,
            };

            metadataResult.Item.SetJavVideoIndex(_jsonSerializer, index);

            var dt = m.GetDate();
            if (dt != null)
                metadataResult.Item.PremiereDate = metadataResult.Item.DateCreated = dt.Value;

            if (!string.IsNullOrWhiteSpace(m.Studio))
                metadataResult.Item.AddStudio(m.Studio);

            if (!string.IsNullOrWhiteSpace(m.Director))
            {
                var pi = new PersonInfo
                {
                    Name = m.Director,
                    Type = PersonType.Director,
                };
                metadataResult.AddPerson(pi);
            }

            if (m.Actors?.Any() == true)
                foreach (var cast in m.Actors)
                {
                    var pi = new PersonInfo
                    {
                        Name = cast,
                        Type = PersonType.Actor,
                    };
                    metadataResult.AddPerson(pi);
                }

            try
            {
                var cachePath = Path.Combine(_appPaths.CachePath, Name, m.Provider, $"{m.Num}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                _jsonSerializer.SerializeToFile(m, cachePath);
            }
            catch
            {
            }

            return metadataResult;
        }

        /// <summary>
        /// 番号最低满足条件：字母、数字、横杠、下划线
        /// </summary>
        private static Regex regexNum = new Regex("^[-_ a-z0-9]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            var list = new List<RemoteSearchResult>();
            if (string.IsNullOrWhiteSpace(searchInfo.Name))
                return list;

            var javid = JavIdRecognizer.Parse(searchInfo.Name);

            _logger?.Info($"{Name}-{nameof(GetSearchResults)} id:{javid?.id} info:{_jsonSerializer.SerializeToString(searchInfo)}");

            //自动搜索的时候，Name=文件夹名称，有时候是不对的，需要跳过
            if (javid == null && (searchInfo.Name.Length > 12 || !regexNum.IsMatch(searchInfo.Name)))
                return list;
            var key = javid?.id ?? searchInfo.Name;
            var scrapers = this.scrapers;
            var enableScrapers = Plugin.Instance?.Configuration?.GetEnableScrapers?.Select(o => o.Name).ToList();
            if (enableScrapers?.Any() == true)
                scrapers = scrapers.Where(o => enableScrapers.Contains(o.Name)).ToList();
            var tasks = scrapers.Select(o => o.Query(key)).ToArray();
            await Task.WhenAll(tasks);
            var all = tasks.Where(o => o.Result?.Any() == true).SelectMany(o => o.Result).ToList();

            _logger?.Info($"{Name}-{nameof(GetSearchResults)} name:{searchInfo.Name} id:{javid?.id} count:{all.Count}");

            if (all.Any() != true)
                return list;

            all = scrapers
                 .Join(all.GroupBy(o => o.Provider),
                 o => o.Name,
                 o => o.Key, (o, v) => v)
                 .SelectMany(o => o)
                 .ToList();

            foreach (var m in all)
            {
                var result = new RemoteSearchResult
                {
                    Name = $"{m.Num} {m.Title}",
                    ProductionYear = m.GetYear(),
                    ImageUrl = Plugin.Instance.Configuration.BuildProxyUrl(m.Cover),
                    SearchProviderName = Name,
                    PremiereDate = m.GetDate(),
                };
                result.SetJavVideoIndex(_jsonSerializer, m);
                list.Add(result);
            }
            return list;
        }
    }
}