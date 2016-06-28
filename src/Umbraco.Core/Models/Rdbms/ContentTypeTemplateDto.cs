﻿using NPoco;
using Umbraco.Core.Persistence;
using Umbraco.Core.Persistence.DatabaseAnnotations;

namespace Umbraco.Core.Models.Rdbms
{
    [TableName("cmsDocumentType")]
    [PrimaryKey("contentTypeNodeId", AutoIncrement = false)]
    [ExplicitColumns]
    internal class ContentTypeTemplateDto
    {
        [Column("contentTypeNodeId")]
        [PrimaryKeyColumn(AutoIncrement = false, Name = "PK_cmsDocumentType", OnColumns = "contentTypeNodeId, templateNodeId")]
        [ForeignKey(typeof(ContentTypeDto), Column = "nodeId")]
        [ForeignKey(typeof(NodeDto))]
        public int ContentTypeNodeId { get; set; }

        [Column("templateNodeId")]
        [ForeignKey(typeof(TemplateDto), Column = "nodeId")]
        public int TemplateNodeId { get; set; }

        [Column("IsDefault")]
        [Constraint(Default = "0")]
        public bool IsDefault { get; set; }

        [ResultColumn]
        [Reference(ReferenceType.OneToOne)]
        public ContentTypeDto ContentTypeDto { get; set; }
    }
}