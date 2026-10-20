//! The right side: overview of the master folder, a version folder or a project with its tabs.

mod backport;
mod changelog;
mod dependencies;
mod git;
mod gradle;
mod overview;
mod project;
mod readme;

use crate::store::{Selection, store};
use leptos::prelude::*;

#[component]
pub fn Details() -> impl IntoView {
    let s = store();
    view! {
        {move || match s.selected.get() {
            Selection::Root => view! { <overview::MasterOverview /> }.into_any(),
            Selection::Version(v) => view! { <overview::VersionDetails version=v /> }.into_any(),
            Selection::Project(path) => view! { <project::ProjectDetails path=path /> }.into_any(),
        }}
    }
}
