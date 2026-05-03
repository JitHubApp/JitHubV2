using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.GitHub;
using JitHub.Models.LegacyGitHub;
using System;
using System.Collections.Generic;

namespace JitHub.Models
{
    public class RepoContentNode : ObservableObject
    {
        private bool _isExpanded;
        private ICollection<RepoContentNode> _children = [];
        private string _content = string.Empty;
        private string _encodedContent = string.Empty;
        private string _name = string.Empty;
        private bool _isDir;
        private string _sha = string.Empty;
        private string _path = string.Empty;
        private StringEnum<EncodingType> _encodingType = JitHub.Models.LegacyGitHub.EncodingType.Utf8;

        public string Content
        {
            get => _content;
            set => SetProperty(ref _content, value);
        }
        public string EncodedContent
        {
            get => _encodedContent;
            set => SetProperty(ref _encodedContent, value);
        }
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
        public bool IsDir
        {
            get => _isDir;
            set => SetProperty(ref _isDir, value);
        }
        public string Sha
        {
            get => _sha;
            set => SetProperty(ref _sha, value);
        }
        public string Path
        {
            get => _path;
            set => SetProperty(ref _path, value);
        }
        public StringEnum<EncodingType> EncodingType
        {
            get => _encodingType;
            set => SetProperty(ref _encodingType, value);
        }
        public ICollection<RepoContentNode> Children
        {
            get => _children;
            set => SetProperty(ref _children, value);
        }
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public bool Loaded { get; set; }

        public RepoContentNode(GitHubRepositoryContent? repoContent)
        {
            Content = repoContent?.Content ?? string.Empty;
            Name = repoContent?.Name ?? string.Empty;
            IsDir = string.Equals(repoContent?.Type, "dir", StringComparison.OrdinalIgnoreCase);
            Sha = repoContent?.Sha ?? string.Empty;
            EncodedContent = repoContent?.Content ?? string.Empty;
            Path = repoContent?.Path ?? string.Empty;
            EncodingType = string.IsNullOrWhiteSpace(repoContent?.Encoding)
                ? JitHub.Models.LegacyGitHub.EncodingType.Utf8
                : new StringEnum<EncodingType>(repoContent.Encoding);
        }

        public RepoContentNode(Blob blob, string name, string path)
        {
            ArgumentNullException.ThrowIfNull(blob);

            Content = blob.Content ?? string.Empty;
            EncodedContent = blob.Content ?? string.Empty;
            Name = name ?? string.Empty;
            IsDir = false;
            Sha = blob.Sha ?? string.Empty;
            EncodingType = blob.Encoding;
            Path = path ?? string.Empty;
        }

        public RepoContentNode WithBlob(Blob blob)
        {
            ArgumentNullException.ThrowIfNull(blob);

            Content = blob.Content ?? string.Empty;
            EncodedContent = blob.Content ?? string.Empty;
            IsDir = false;
            Sha = blob.Sha ?? string.Empty;
            EncodingType = blob.Encoding;
            return this;
        }
    }
}
