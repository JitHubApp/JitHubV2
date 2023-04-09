using CommunityToolkit.Mvvm.ComponentModel;
using Octokit;
using System;
using System.Collections.Generic;

namespace JitHub.Models
{
    public class RepoContentNode : ObservableObject
    {
        private bool _isExpanded;
        private ICollection<RepoContentNode> _children;
        //private RepositoryContent _content;
        private string _content;
        private string _encodedContent;
        private string _name;
        private bool _isDir;
        private string _sha;
        private string _path;
        private StringEnum<EncodingType> _encodingType;


        //public RepositoryContent Content 
        //{
        //    get => _content;
        //    set => SetProperty(ref _content, value);
        //}
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

        public RepoContentNode(RepositoryContent repoContent)
        {
            Content = repoContent?.Content;
            Name = repoContent?.Name;
            IsDir = repoContent?.Type == ContentType.Dir;
            Sha = repoContent?.Sha;
            //EncodingType = repoContent?.Encoding.ToLower() == ;
            EncodedContent = repoContent?.EncodedContent;
            Path = repoContent.Path;
        }

        public RepoContentNode(Blob blob, string name, string path)
        {
            Content = blob.Content;
            EncodedContent = blob.Content;
            Name = name;
            IsDir = false;
            Sha = blob.Sha;
            EncodingType = blob.Encoding;
            Path = path;
        }

        public RepoContentNode WithBlob(Blob blob)
        {
            Content = blob.Content;
            EncodedContent = blob.Content;
            IsDir = false;
            Sha = blob.Sha;
            EncodingType = blob.Encoding;
            return this;
        }
    }
}
