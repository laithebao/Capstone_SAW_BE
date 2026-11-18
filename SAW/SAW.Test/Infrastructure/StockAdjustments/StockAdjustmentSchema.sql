-- Stock-adjustment constraints used by the isolated SQL fixtures. Reviewed 2026-11-12.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_STOCK_ADJUSTMENT_Approval') ALTER TABLE [dbo].[STOCK_ADJUSTMENT] ADD CONSTRAINT [CK_STOCK_ADJUSTMENT_Approval] CHECK ([AdjustmentStatus]<>N'APPROVED' OR [ReviewedByAccountID] IS NOT NULL)
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_STOCK_ADJUSTMENT_Quantity') ALTER TABLE [dbo].[STOCK_ADJUSTMENT] ADD CONSTRAINT [CK_STOCK_ADJUSTMENT_Quantity] CHECK ([SystemQuantity]>=(0) AND [ActualQuantity]>=(0))
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_STOCK_ADJUSTMENT_RequestKind') ALTER TABLE [dbo].[STOCK_ADJUSTMENT] ADD CONSTRAINT [CK_STOCK_ADJUSTMENT_RequestKind] CHECK ([RequestKind]='DISPOSAL' OR [RequestKind]='COUNT')
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_STOCK_ADJUSTMENT_Status') ALTER TABLE [dbo].[STOCK_ADJUSTMENT] ADD CONSTRAINT [CK_STOCK_ADJUSTMENT_Status] CHECK ([AdjustmentStatus]=N'REJECTED' OR [AdjustmentStatus]=N'APPROVED' OR [AdjustmentStatus]=N'PENDING')
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_STOCK_ADJUSTMENT_Type') ALTER TABLE [dbo].[STOCK_ADJUSTMENT] ADD CONSTRAINT [CK_STOCK_ADJUSTMENT_Type] CHECK ([AdjustmentType]=N'OTHER' OR [AdjustmentType]=N'COUNTING_ERROR' OR [AdjustmentType]=N'SPOILAGE' OR [AdjustmentType]=N'DAMAGE')
GO
