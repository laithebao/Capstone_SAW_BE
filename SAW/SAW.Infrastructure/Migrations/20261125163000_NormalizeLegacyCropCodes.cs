using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SAW.Infrastructure.Persistence;
#nullable disable
namespace SAW.Infrastructure.Migrations;
[DbContext(typeof(AppDbContext))]
[Migration("20261125163000_NormalizeLegacyCropCodes")]
public sealed class NormalizeLegacyCropCodes : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        SET XACT_ABORT ON;
        CREATE TABLE dbo.CROP_CODE_MIGRATION_01 (
            CropTypeId int NOT NULL PRIMARY KEY, OldCode nvarchar(50) NOT NULL, NewCode nvarchar(50) NOT NULL,
            OldCategoryName nvarchar(100) NOT NULL, OldCategoryId int NULL, OriginalCreatedAt datetime2 NOT NULL,
            DateRule nvarchar(80) NOT NULL);
        DECLARE @roots TABLE(Code nvarchar(30), Name nvarchar(100), SortOrder int);
        INSERT @roots VALUES ('NC',N'Ngũ cốc',0),('RC',N'Rau củ',1),('TC',N'Trái cây',2);
        IF EXISTS(SELECT 1 FROM CATEGORY c JOIN @roots r ON c.Code=r.Code WHERE c.ParentId IS NOT NULL OR c.Name<>r.Name)
            THROW 51100, 'Category code conflicts with approved mapping.', 1;
        INSERT CATEGORY(Code,Name,ParentId,IsActive,SortOrder,CreatedAt)
            SELECT r.Code,r.Name,NULL,1,r.SortOrder,SYSUTCDATETIME() FROM @roots r WHERE NOT EXISTS(SELECT 1 FROM CATEGORY c WHERE c.Code=r.Code);
        DECLARE @children TABLE(Code nvarchar(30), Name nvarchar(100), ParentCode nvarchar(30), SortOrder int);
        INSERT @children VALUES ('GAO',N'Gạo','NC',0),('KHOAITAY',N'Khoai tây','RC',0),('CAROT',N'Cà rốt','RC',1),('XOAI',N'Xoài','TC',0),('THANHLONG',N'Thanh long','TC',1);
        IF EXISTS(SELECT 1 FROM CATEGORY c JOIN @children r ON c.Code=r.Code JOIN CATEGORY p ON p.Code=r.ParentCode WHERE c.ParentId IS NULL OR c.ParentId<>p.CategoryId OR c.Name<>r.Name)
            THROW 51101, 'Child category code conflicts with approved mapping.', 1;
        INSERT CATEGORY(Code,Name,ParentId,IsActive,SortOrder,CreatedAt)
            SELECT r.Code,r.Name,p.CategoryId,1,r.SortOrder,SYSUTCDATETIME() FROM @children r JOIN CATEGORY p ON p.Code=r.ParentCode WHERE NOT EXISTS(SELECT 1 FROM CATEGORY c WHERE c.Code=r.Code);
        DECLARE @map TABLE(OldCode nvarchar(50), Prefix nvarchar(40), ChildCode nvarchar(30), IsUtc bit);
        INSERT @map VALUES
            ('RICE-ST25','NC_GAO_ST25_','GAO',0),('POTATO-DL','RC_KHOAITAY_DALAT_','KHOAITAY',0),('CARROT-DL','RC_CAROT_DALAT_','CAROT',0),
            ('DEMO-DIST-RICE','NC_GAO_DEMO_','GAO',1),('DEMO-DIST-MANGO','TC_XOAI_DEMO_','XOAI',1),('DEMO-DIST-DRAGON','TC_THANHLONG_DEMO_','THANHLONG',1);
        IF EXISTS(SELECT 1 FROM CROP_TYPE c JOIN @map m ON m.OldCode=c.CropCode WHERE c.CreatedAt IS NULL)
            THROW 51102, 'CreatedAt is required; no substitute migration date is permitted.', 1;
        -- Approved legacy convention: retain the stored calendar date where original
        -- timezone cannot be established. Demo seed explicitly uses SYSUTCDATETIME.
        INSERT CROP_CODE_MIGRATION_01
            SELECT c.CropTypeID,c.CropCode,m.Prefix+REPLACE(CONVERT(nvarchar(10),CASE WHEN m.IsUtc=1 THEN DATEADD(hour,7,c.CreatedAt) ELSE c.CreatedAt END,103),'/',''),
                c.CategoryName,c.CategoryId,c.CreatedAt,CASE WHEN m.IsUtc=1 THEN N'UTC seed -> Asia/Ho_Chi_Minh' ELSE N'Approved legacy stored calendar date' END
            FROM CROP_TYPE c JOIN @map m ON m.OldCode=c.CropCode;
        IF EXISTS(SELECT NewCode FROM CROP_CODE_MIGRATION_01 GROUP BY NewCode HAVING COUNT(*)>1)
            OR EXISTS(SELECT 1 FROM CROP_CODE_MIGRATION_01 m JOIN CROP_TYPE c ON c.CropCode=m.NewCode AND c.CropTypeID<>m.CropTypeId)
            OR EXISTS(SELECT 1 FROM CROP_CODE_MIGRATION_01 WHERE LEN(NewCode)>50)
            THROW 51103, 'New crop code collision or length violation; migration cancelled.', 1;
        UPDATE c SET CropCode=a.NewCode,CategoryId=child.CategoryId,CategoryName=parent.Name
            FROM CROP_TYPE c JOIN CROP_CODE_MIGRATION_01 a ON a.CropTypeId=c.CropTypeID
            JOIN @map m ON m.OldCode=a.OldCode JOIN CATEGORY child ON child.Code=m.ChildCode JOIN CATEGORY parent ON parent.CategoryId=child.ParentId;
        -- Preserve evidence rather than inferring a legal representative from a contact.
        UPDATE SUPPLIER SET LegalRepresentative=LTRIM(RTRIM(SUBSTRING(ContactPerson,CHARINDEX(N'| Đại diện PL:',ContactPerson)+LEN(N'| Đại diện PL:'),150)))
            WHERE LegalRepresentative IS NULL AND CHARINDEX(N'| Đại diện PL:',ContactPerson)>0
            AND LEN(LTRIM(RTRIM(SUBSTRING(ContactPerson,CHARINDEX(N'| Đại diện PL:',ContactPerson)+LEN(N'| Đại diện PL:'),150)))) BETWEEN 1 AND 100;
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        IF EXISTS(SELECT 1 FROM SUPPLY_CONTRACT_REVISION) OR EXISTS(SELECT 1 FROM EMAIL_OUTBOX)
            OR EXISTS(SELECT 1 FROM GROWING_AREA WHERE OwnerSupplierId IS NOT NULL)
            OR EXISTS(SELECT 1 FROM CATEGORY WHERE Code NOT IN ('NC','RC','TC','GAO','KHOAITAY','CAROT','XOAI','THANHLONG'))
            OR EXISTS(SELECT 1 FROM CATEGORY WHERE UpdatedAt IS NOT NULL)
            OR EXISTS(SELECT 1 FROM CROP_TYPE WHERE CategoryId IS NOT NULL AND CropTypeID NOT IN (SELECT CropTypeId FROM CROP_CODE_MIGRATION_01))
            THROW 51107, 'New business data exists. Export and review the downgrade before restoring legacy codes.', 1;
        IF EXISTS(SELECT 1 FROM CROP_CODE_MIGRATION_01 a LEFT JOIN CROP_TYPE c ON c.CropTypeID=a.CropTypeId WHERE c.CropTypeID IS NULL OR c.CropCode<>a.NewCode OR c.CreatedAt<>a.OriginalCreatedAt)
            THROW 51104, 'Crop mapping changed since migration. Export and review before rollback.', 1;
        IF EXISTS(SELECT 1 FROM CROP_CODE_MIGRATION_01 a JOIN CROP_TYPE c ON c.CropCode=a.OldCode AND c.CropTypeID<>a.CropTypeId)
            THROW 51105, 'Old code is in use. Rollback cancelled.', 1;
        UPDATE c SET CropCode=a.OldCode,CategoryId=a.OldCategoryId,CategoryName=a.OldCategoryName FROM CROP_TYPE c JOIN CROP_CODE_MIGRATION_01 a ON a.CropTypeId=c.CropTypeID;
        -- Keep newly referenced seed categories; foundation Down then refuses data loss.
        DELETE c FROM CATEGORY c WHERE c.Code IN ('GAO','KHOAITAY','CAROT','XOAI','THANHLONG') AND NOT EXISTS(SELECT 1 FROM CROP_TYPE t WHERE t.CategoryId=c.CategoryId);
        DELETE c FROM CATEGORY c WHERE c.Code IN ('NC','RC','TC') AND NOT EXISTS(SELECT 1 FROM CATEGORY child WHERE child.ParentId=c.CategoryId) AND NOT EXISTS(SELECT 1 FROM CROP_TYPE t WHERE t.CategoryId=c.CategoryId);
        DROP TABLE CROP_CODE_MIGRATION_01;
        """);
}
