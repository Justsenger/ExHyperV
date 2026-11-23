export interface CandidateItem {
  attr: string;
  value: string;
  label: string;
  icon?: string;
  active?: boolean;
}

export interface CandidateSection {
  id: string;
  subhead: string;
  items: CandidateItem[];
}

export interface CandidatePanelConfig {
  title: string;
  sections: CandidateSection[];
  mappings: [string, string][];
}

export const HOST_CANDIDATE_PANEL: CandidatePanelConfig = {
  title: '',
  sections: [],
  mappings: []
};
