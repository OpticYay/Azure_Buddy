// Mirrors AzureBuddy.Core/WorkItemStates/WorkItemStateModels.cs.

export interface WorkItemStateView {
  id: number;
  workItemType: string;
  stateName: string;
  displayOrder: number;
  isEnabled: boolean;
  updatedAt: string;
}

export interface WorkItemTypeStatesView {
  workItemType: string;
  states: WorkItemStateView[];
}

export interface CreateWorkItemStateRequest {
  workItemType: string;
  stateName: string;
  displayOrder: number;
  isEnabled: boolean;
}

export interface UpdateWorkItemStateRequest {
  stateName: string;
  displayOrder: number;
  isEnabled: boolean;
}
